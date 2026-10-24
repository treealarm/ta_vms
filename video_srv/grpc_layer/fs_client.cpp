#include "fs_client.h"

#include <cstdlib>
#include <fstream>
#include <format>
#include <iomanip>
#include <utility>

#include "google/protobuf/timestamp.pb.h"
#include <google/protobuf/util/json_util.h>

#include "file_system/recording_storage.h"
#include "logger/logger.h"

namespace {
constexpr std::size_t k_outbox_load_batch_size = 50;
constexpr const char* k_outbox_file_name = ".fs_ingest_outbox.jsonl";
constexpr const char* k_permanent_failures_file_name = ".fs_ingest_permanent_failures.jsonl";

std::string require_env(const char* key)
{
  const char* value = std::getenv(key);
  if (!value || *value == '\0')
  {
    const char* app_id = std::getenv("APP_ID");
    throw std::runtime_error(std::format(
      "Required environment variable '{}' is not set (APP_ID={})",
      key, app_id ? app_id : "<not set>"));
  }
  return value;
}

static std::string s_fs_app_id;

google::protobuf::Timestamp to_proto_ts(std::chrono::system_clock::time_point value)
{
  const auto seconds = std::chrono::floor<std::chrono::seconds>(value);
  const auto nanos = std::chrono::duration_cast<std::chrono::nanoseconds>(value - seconds);

  google::protobuf::Timestamp timestamp;
  timestamp.set_seconds(seconds.time_since_epoch().count());
  timestamp.set_nanos(static_cast<int>(nanos.count()));
  return timestamp;
}


bool is_retryable_grpc_error(grpc::StatusCode code)
{
  return code == grpc::StatusCode::UNAVAILABLE
    || code == grpc::StatusCode::DEADLINE_EXCEEDED;
}

std::string request_file_path(const vms::fs::IngestClipRequest& request)
{
  if (!request.has_clip())
  {
    return {};
  }

  return request.clip().file_path();
}
}

void fs_client::set_fs_app_id(const std::string& id) { s_fs_app_id = id; }

fs_client::fs_client()
  : m_channel(grpc::CreateChannel("127.0.0.1:" + require_env("DAPR_GRPC_PORT"), grpc::InsecureChannelCredentials())),
    m_stub(vms::fs::FsService::NewStub(m_channel)),
    m_outbox_path(recording_storage::resolve_storage_base_path() / k_outbox_file_name),
    m_permanent_failures_path(recording_storage::resolve_storage_base_path() / k_permanent_failures_file_name)
{
  if (s_fs_app_id.empty())
    throw std::runtime_error("fs_client: call set_fs_app_id() before first use");
  m_app_id = s_fs_app_id;
  std::lock_guard<std::mutex> lock(m_mutex);
  load_outbox_chunk_locked(k_outbox_load_batch_size);
  m_worker = std::thread(&fs_client::worker_loop, this);
  m_cv.notify_one();
}

fs_client& fs_client::instance()
{
  static fs_client client;
  return client;
}

fs_client::~fs_client()
{
  {
    std::lock_guard<std::mutex> lock(m_mutex);
    m_stopping = true;
  }
  m_cv.notify_all();
  if (m_worker.joinable())
  {
    m_worker.join();
  }
}

void fs_client::enqueue_ingest_clip(
  const std::string& camera_id,
  const std::string& stream_name,
  const std::string& storage_id,
  const std::string& file_path,
  std::chrono::system_clock::time_point start_ts,
  std::optional<std::chrono::system_clock::time_point> end_ts,
  std::uint64_t size_bytes,
  bool force_retained,
  const std::string& origin)
{
  vms::fs::IngestClipRequest request;
  request.set_force_retained(force_retained);
  auto* clip = request.mutable_clip();
  clip->set_camera_id(camera_id);
  clip->set_stream_name(stream_name);
  clip->set_storage_id(storage_id);
  clip->set_file_path(file_path);
  if (!origin.empty())
  {
    clip->set_origin(origin);
  }
  *clip->mutable_start_ts() = to_proto_ts(start_ts);
  if (end_ts.has_value())
  {
    *clip->mutable_end_ts() = to_proto_ts(*end_ts);
  }
  if (size_bytes > 0)
  {
    clip->set_size_bytes(size_bytes);
  }

  {
    std::lock_guard<std::mutex> lock(m_mutex);
    if (m_stopping)
    {
      logger::instance().warning("fs_client dispatcher is stopping; drop IngestClip task");
      return;
    }

    m_queue.push(std::move(request));
  }
  m_cv.notify_one();
}

void fs_client::report_source_liveness(
  const std::string& server_id,
  const std::string& instance_id,
  const std::vector<liveness_entry>& streams)
{
  if (!m_stub || streams.empty())
  {
    return;
  }

  vms::fs::ReportSourceLivenessRequest request;
  request.set_server_id(server_id);
  request.set_instance_id(instance_id);
  for (const auto& s : streams)
  {
    auto* item = request.add_streams();
    item->set_camera_id(s.camera_id);
    item->set_stream_name(s.stream_name);
    item->set_alive(s.alive);
    item->set_recording_expected(s.recording_expected);
    if (s.last_frame_ts.has_value())
    {
      *item->mutable_last_frame_ts() = to_proto_ts(*s.last_frame_ts);
    }
    if (s.recovered_frame_ts.has_value())
    {
      *item->mutable_recovered_frame_ts() = to_proto_ts(*s.recovered_frame_ts);
    }
  }

  vms::fs::OperationStatus response;
  grpc::ClientContext context;
  context.set_deadline(std::chrono::system_clock::now() + std::chrono::seconds(2));
  context.AddMetadata("dapr-app-id", m_app_id);

  const grpc::Status status = m_stub->ReportSourceLiveness(&context, request, &response);
  if (!status.ok())
  {
    // Warned, not retried: the next tick carries the same state, and an outage boundary comes from
    // last_frame_ts rather than from when the report happened to arrive.
    logger::instance().warning(std::format(
      "ReportSourceLiveness via Dapr app '{}' failed: {}", m_app_id, status.error_message()));
  }
}

bool fs_client::ingest_clip(
  const recording& clip,
  std::string& clip_uid,
  std::string& error)
{
  vms::fs::IngestClipRequest request;
  auto* meta = request.mutable_clip();
  meta->set_camera_id(clip.camera_id);
  meta->set_stream_name(clip.stream_name);
  meta->set_file_path(clip.file_path);
  *meta->mutable_start_ts() = to_proto_ts(clip.start_ts);
  if (clip.end_ts)
  {
    *meta->mutable_end_ts() = to_proto_ts(*clip.end_ts);
  }
  meta->set_size_bytes(clip.size_bytes);

  const auto outcome = ingest_clip_sync(request);
  if (outcome.result != ingest_result::success)
  {
    error = outcome.reason;
    return false;
  }

  clip_uid.clear();
  return true;
}

void fs_client::worker_loop()
{
  while (true)
  {
    vms::fs::IngestClipRequest request;
    std::string file_path;
    {
      std::unique_lock<std::mutex> lock(m_mutex);
      if (m_queue.empty())
      {
        load_outbox_chunk_locked(k_outbox_load_batch_size);
      }
      m_cv.wait(lock, [this] { return m_stopping || !m_queue.empty(); });
      if (m_stopping && m_queue.empty())
      {
        return;
      }

      request = std::move(m_queue.front());
      m_queue.pop();
      file_path = request_file_path(request);
    }

    const auto outcome = ingest_clip_sync(request);
    if (outcome.result == ingest_result::retryable_failure)
    {
      std::lock_guard<std::mutex> lock(m_mutex);
      if (request.has_clip() && request.clip().size_bytes() > 0)
      {
        if (!append_outbox_locked(request))
        {
          logger::instance().warning(std::format(
            "drop retryable close ingest for '{}' because outbox append failed",
            file_path));
        }
      }
    }
    else if (outcome.result == ingest_result::permanent_failure)
    {
      std::lock_guard<std::mutex> lock(m_mutex);
      if (!append_permanent_failure_locked(request, outcome.reason))
      {
        logger::instance().error(std::format(
          "failed to write permanent ingest failure for '{}'",
          file_path));
      }
    }
  }
}

fs_client::ingest_outcome fs_client::ingest_clip_sync(const vms::fs::IngestClipRequest& request)
{
  if (!m_stub)
  {
    return { ingest_result::retryable_failure, "grpc stub is not initialized" };
  }

  const std::string file_path = request_file_path(request);

  vms::fs::OperationStatus response;
  grpc::ClientContext context;
  context.set_deadline(std::chrono::system_clock::now() + std::chrono::seconds(2));
  context.AddMetadata("dapr-app-id", m_app_id);

  const grpc::Status status = m_stub->IngestClip(&context, request, &response);
  if (!status.ok())
  {
    const std::string error = std::format(
      "rpc status={} message={}",
      static_cast<int>(status.error_code()),
      status.error_message());
    if (is_retryable_grpc_error(status.error_code()))
    {
      logger::instance().warning(std::format(
        "IngestClip transient failure for '{}' via Dapr app '{}': {}",
        file_path,
        m_app_id,
        error));
      return { ingest_result::retryable_failure, error };
    }

    logger::instance().error(std::format(
      "IngestClip permanent transport failure for '{}' via Dapr app '{}': {}",
      file_path,
      m_app_id,
      error));
    return { ingest_result::permanent_failure, error };
  }

  switch (response.status())
  {
  case vms::fs::OperationStatus::SUCCESS:
    return { ingest_result::success, {} };
  case vms::fs::OperationStatus::RETRYABLE_FAILURE:
    logger::instance().warning(std::format(
      "IngestClip retryable server failure for '{}': {}",
      file_path,
      response.message()));
    return { ingest_result::retryable_failure, response.message() };
  case vms::fs::OperationStatus::PERMANENT_FAILURE:
    logger::instance().error(std::format(
      "IngestClip permanent server failure for '{}': {}",
      file_path,
      response.message()));
    return { ingest_result::permanent_failure, response.message() };
  case vms::fs::OperationStatus::STATUS_UNSPECIFIED:
  default:
    logger::instance().error(std::format(
      "IngestClip unknown server status for '{}': {}",
      file_path,
      response.message()));
    return { ingest_result::permanent_failure, response.message() };
  }
}

bool fs_client::load_outbox_chunk_locked(std::size_t max_records)
{
  std::lock_guard<std::mutex> file_lock(m_file_mutex);
  if (!std::filesystem::exists(m_outbox_path))
  {
    return false;
  }

  std::ifstream in(m_outbox_path, std::ios::binary);
  if (!in.is_open())
  {
    return false;
  }

  in.seekg(0, std::ios::end);
  const std::streamoff file_size = in.tellg();
  if (file_size <= 0)
  {
    return false;
  }

  std::streamoff start_offset = file_size;
  std::size_t lines_found = 0;
  while (start_offset > 0 && lines_found < max_records)
  {
    --start_offset;
    in.seekg(start_offset);
    char ch = '\0';
    in.get(ch);
    if (ch == '\n' && start_offset < file_size - 1)
    {
      ++lines_found;
    }
  }
  if (start_offset > 0)
  {
    ++start_offset;
  }

  const std::streamsize chunk_size = static_cast<std::streamsize>(file_size - start_offset);
  if (chunk_size <= 0)
  {
    return false;
  }
  std::string chunk(static_cast<std::size_t>(chunk_size), '\0');
  in.seekg(start_offset);
  in.read(chunk.data(), chunk_size);
  if (!in && in.gcount() != chunk_size)
  {
    return false;
  }
  in.close();

  std::size_t loaded = 0;
  std::size_t pos = 0;
  while (pos < chunk.size())
  {
    const std::size_t eol = chunk.find('\n', pos);
    const std::size_t len = (eol == std::string::npos) ? (chunk.size() - pos) : (eol - pos);
    std::string line = chunk.substr(pos, len);
    pos = (eol == std::string::npos) ? chunk.size() : eol + 1;
    if (line.empty())
    {
      continue;
    }

    vms::fs::IngestClipRequest request;
    const auto parse_status = google::protobuf::util::JsonStringToMessage(line, &request);
    if (!parse_status.ok() || !request.has_clip())
    {
      logger::instance().warning(std::format(
        "failed to parse fs outbox line, skipping: {}",
        parse_status.ToString()));
      continue;
    }
    if (request.clip().size_bytes() == 0)
    {
      continue;
    }
    m_queue.push(std::move(request));
    ++loaded;
  }

  std::error_code ec;
  std::filesystem::resize_file(m_outbox_path, static_cast<std::uintmax_t>(start_offset), ec);
  if (ec)
  {
    logger::instance().warning(std::format(
      "failed to truncate outbox file '{}': {}",
      m_outbox_path.string(),
      ec.message()));
  }

  return loaded > 0;
}

bool fs_client::append_outbox_locked(const vms::fs::IngestClipRequest& request) const
{
  std::lock_guard<std::mutex> file_lock(m_file_mutex);
  std::error_code ec;
  const auto dir = m_outbox_path.parent_path();
  if (!dir.empty())
  {
    std::filesystem::create_directories(dir, ec);
  }

  std::ofstream out(m_outbox_path, std::ios::app);
  if (!out.is_open())
  {
    return false;
  }

  std::string json;
  const auto status = google::protobuf::util::MessageToJsonString(request, &json);
  if (!status.ok())
  {
    return false;
  }

  out << json << '\n';
  return static_cast<bool>(out);
}

std::vector<fs_client::storage_info> fs_client::get_camera_storages(const std::string& camera_id)
{
  std::vector<storage_info> cached;
  bool cache_hit = false;
  bool stale = false;

  {
    std::lock_guard lock(m_storage_mutex);
    auto it = m_storage_cache.find(camera_id);
    if (it != m_storage_cache.end())
    {
      const auto age = std::chrono::steady_clock::now() - it->second.fetched_at;
      stale = (age >= std::chrono::minutes(1));
      cached = it->second.storages;
      cache_hit = true;
    }
  }

  if (cache_hit)
  {
    if (stale)
      trigger_storage_refresh(camera_id);
    return cached;
  }

  auto storages = fetch_camera_storages(camera_id).value_or(std::vector<storage_info>{});
  // An empty result is not cached: a momentary RPC failure looks exactly like "nothing assigned",
  // and caching it would keep recording and imports broken for a further minute after the cause is
  // gone. A real "no storages" answer costs one cheap round trip per call instead.
  if (!storages.empty())
  {
    std::lock_guard lock(m_storage_mutex);
    m_storage_cache[camera_id] = { storages, std::chrono::steady_clock::now() };
  }
  return storages;
}
void fs_client::invalidate_storage_cache()
{
  std::lock_guard lock(m_storage_mutex);
  m_storage_cache.clear();
}

std::optional<std::vector<fs_client::storage_info>> fs_client::fetch_camera_storages(
  const std::string& camera_id)
{
  if (!m_stub)
    return std::nullopt;

  vms::fs::GetCameraStorageRequest request;
  request.set_camera_id(camera_id);

  vms::fs::GetCameraStorageResponse response;
  grpc::ClientContext context;
  context.set_deadline(std::chrono::system_clock::now() + std::chrono::seconds(2));
  context.AddMetadata("dapr-app-id", m_app_id);

  const grpc::Status status = m_stub->GetCameraStorage(&context, request, &response);
  if (!status.ok())
  {
    // Logged because the caller cannot tell this apart from "the camera has no storage assigned",
    // and both surface to the operator as the same "no writable storage" message.
    logger::instance().warning(std::format(
      "GetCameraStorage RPC via Dapr failed for app '{}' camera='{}': {}",
      m_app_id, camera_id, status.error_message()));
    return std::nullopt;
  }

  std::vector<storage_info> result;
  for (const auto& s : response.storages())
    result.push_back({ s.storage_id(), s.root_path() });

  if (result.empty())
  {
    logger::instance().warning(std::format(
      "no storage is assigned to camera '{}' — recording and archive import have nowhere to write",
      camera_id));
  }
  return result;
}

void fs_client::trigger_storage_refresh(const std::string& camera_id)
{
  {
    std::lock_guard lock(m_storage_mutex);
    if (!m_storage_refreshing.insert(camera_id).second)
      return;
  }

  std::thread([this, camera_id]() {
    auto storages = fetch_camera_storages(camera_id);
    std::lock_guard lock(m_storage_mutex);
    // A failed call keeps the list last read, still marked stale, so the next lookup tries again;
    // storing an empty one would stop recording for a minute over one lost round trip.
    if (storages)
      m_storage_cache[camera_id] = { std::move(*storages), std::chrono::steady_clock::now() };
    m_storage_refreshing.erase(camera_id);
  }).detach();
}

bool fs_client::append_permanent_failure_locked(
  const vms::fs::IngestClipRequest& request,
  const std::string& reason) const
{
  std::lock_guard<std::mutex> file_lock(m_file_mutex);
  std::error_code ec;
  const auto dir = m_permanent_failures_path.parent_path();
  if (!dir.empty())
  {
    std::filesystem::create_directories(dir, ec);
  }

  std::string request_json;
  const auto req_status = google::protobuf::util::MessageToJsonString(request, &request_json);
  if (!req_status.ok())
  {
    return false;
  }

  std::ofstream out(m_permanent_failures_path, std::ios::app);
  if (!out.is_open())
  {
    return false;
  }

  out << "{\"reason\": " << std::quoted(reason) << ", \"request\": " << request_json << "}\n";
  return static_cast<bool>(out);
}
