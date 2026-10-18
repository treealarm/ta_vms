#include "onvif_metadata_client.h"

#include <algorithm>
#include <chrono>
#include <cstdlib>
#include <format>
#include <iterator>
#include <utility>

#include "google/protobuf/timestamp.pb.h"

#include "logger/logger.h"

namespace {

std::string require_env(const char* key)
{
  const char* value = std::getenv(key);
  if (!value || *value == '\0')
    throw std::runtime_error(std::format("Required environment variable '{}' is not set", key));
  return value;
}

google::protobuf::Timestamp to_proto_ts(std::chrono::system_clock::time_point value)
{
  const auto seconds = std::chrono::floor<std::chrono::seconds>(value);
  const auto nanos = std::chrono::duration_cast<std::chrono::nanoseconds>(value - seconds);

  google::protobuf::Timestamp timestamp;
  timestamp.set_seconds(seconds.time_since_epoch().count());
  timestamp.set_nanos(static_cast<int>(nanos.count()));
  return timestamp;
}

/// Floor on the gap between two RPCs. Not a delay before sending: a document arriving on an idle
/// sender goes out at once, because by then this much has always already elapsed. It only bounds
/// how often the sender may fire, and the documents that accumulate while it waits out the
/// remainder leave together — which is where the batching comes from on a busy node.
///
/// This replaced an unconditional 200 ms flush window. The window applied its full cost to every
/// document, including a lone one, and since the picture reaches the browser by a path that does
/// not pass through here, that was a flat addition to how far the boxes trailed their objects.
constexpr auto kMinSendInterval = std::chrono::milliseconds(50);

/// Queue ceiling. Roughly a second of the noisiest cameras a node is likely to carry; past that
/// the sidecar is not keeping up and the oldest documents are the least worth keeping.
constexpr size_t kMaxQueued = 200;

/// Cap on one RPC, so a backlog drains over several calls rather than in one oversized message.
constexpr size_t kMaxBatch = 64;

}  // namespace

onvif_metadata_client& onvif_metadata_client::instance()
{
  static onvif_metadata_client client;
  return client;
}

onvif_metadata_client::~onvif_metadata_client()
{
  stop();
}

void onvif_metadata_client::start()
{
  if (m_running)
    return;

  m_app_id = require_env("DAPR_ONVIF_APP_ID");
  m_channel = grpc::CreateChannel(
    "127.0.0.1:" + require_env("DAPR_GRPC_PORT"), grpc::InsecureChannelCredentials());
  m_stub = vms::onvif::OnvifService::NewStub(m_channel);

  m_running = true;
  m_thread = std::thread([this] { send_loop(); });

  logger::instance().info(std::format(
    "ONVIF metadata forwarding enabled, target app '{}'", m_app_id));
}

void onvif_metadata_client::stop()
{
  if (!m_running.exchange(false))
    return;

  m_cv.notify_all();
  if (m_thread.joinable())
    m_thread.join();
}

void onvif_metadata_client::enqueue(
  const std::string& camera_id,
  const std::string& stream_name,
  std::string&& xml,
  int frame_width,
  int frame_height,
  std::chrono::system_clock::time_point captured_at)
{
  if (!m_running)
    return;

  vms::onvif::MetadataDocument document;
  document.set_camera_id(camera_id);
  document.set_stream_name(stream_name);
  document.set_xml(std::move(xml));
  document.set_frame_width(frame_width);
  document.set_frame_height(frame_height);
  // The reader's stamp, not now(): a document is assembled from several RTP packets and then waits
  // its turn in this queue, so now() would place a box later than the frame it describes by however
  // long both took. These boxes are drawn over video — live and archived — whose own timeline comes
  // off exactly this clock.
  *document.mutable_received_at() = to_proto_ts(captured_at);

  {
    std::lock_guard<std::mutex> lock(m_mutex);
    m_queue.push_back(std::move(document));
    while (m_queue.size() > kMaxQueued)
    {
      m_queue.pop_front();
      ++m_dropped;
    }
  }
  m_cv.notify_one();
}

void onvif_metadata_client::send_loop()
{
  uint64_t reported_drops = 0;

  while (m_running)
  {
    std::deque<vms::onvif::MetadataDocument> batch;
    uint64_t dropped_snapshot = 0;
    {
      std::unique_lock<std::mutex> lock(m_mutex);

      // Wait for something to send.
      m_cv.wait(lock, [this] { return !m_running || !m_queue.empty(); });
      if (!m_running && m_queue.empty())
        return;

      // Then hold only for as much of kMinSendInterval as has not already passed. After an idle
      // stretch that is nothing at all and the document leaves immediately; under load it is what
      // makes the next batch a batch. Relying on the RPC itself to provide that coalescing does not
      // work — the sidecar is on loopback and answers faster than a chatty node produces documents,
      // so every one of them would become its own call.
      const auto since_last = std::chrono::steady_clock::now() - m_last_send;
      if (since_last < kMinSendInterval)
      {
        m_cv.wait_for(lock, kMinSendInterval - since_last, [this] { return !m_running; });
        if (!m_running && m_queue.empty())
          return;
      }

      const size_t take = std::min(m_queue.size(), kMaxBatch);
      batch.insert(
        batch.end(),
        std::make_move_iterator(m_queue.begin()),
        std::make_move_iterator(m_queue.begin() + static_cast<std::ptrdiff_t>(take)));
      m_queue.erase(m_queue.begin(), m_queue.begin() + static_cast<std::ptrdiff_t>(take));
      dropped_snapshot = m_dropped;
    }

    if (dropped_snapshot != reported_drops)
    {
      logger::instance().warning(std::format(
        "ONVIF metadata: queue overflow, {} document(s) dropped in total", dropped_snapshot));
      reported_drops = dropped_snapshot;
    }

    if (!batch.empty())
    {
      m_last_send = std::chrono::steady_clock::now();
      send_batch(batch);
    }
  }
}

void onvif_metadata_client::send_batch(std::deque<vms::onvif::MetadataDocument>& batch)
{
  vms::onvif::MetadataDocumentBatch request;
  for (auto& document : batch)
    *request.add_documents() = std::move(document);

  vms::onvif::PushMetadataDocumentsResponse response;

  grpc::ClientContext context;
  context.set_deadline(std::chrono::system_clock::now() + std::chrono::seconds(5));
  context.AddMetadata("dapr-app-id", m_app_id);

  const grpc::Status status = m_stub->PushMetadataDocuments(&context, request, &response);
  if (!status.ok())
  {
    // Logged, not retried: the overlay wants fresh boxes, and re-sending a batch that is already
    // hundreds of milliseconds old only delays the ones behind it.
    logger::instance().warning(std::format(
      "PushMetadataDocuments RPC via Dapr failed for app '{}' ({} documents): {}",
      m_app_id, request.documents_size(), status.error_message()));
  }
}
