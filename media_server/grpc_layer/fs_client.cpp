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

std::string read_env_or_default(const char* key, const char* fallback)
{
  const char* value = std::getenv(key);
  if (!value || *value == '\0') {
    return fallback;
  }

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

std::chrono::system_clock::time_point from_timestamp(
  const google::protobuf::Timestamp& value)
{
  return std::chrono::system_clock::time_point{
    std::chrono::seconds(value.seconds()) + std::chrono::nanoseconds(value.nanos())};
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

fs_client::fs_client()
  :
    m_app_id(read_env_or_default("DAPR_FS_APP_ID", "vmsfs")),
    m_channel(grpc::CreateChannel(
      read_env_or_default("DAPR_GRPC_ENDPOINT", "127.0.0.1:50001"),
      grpc::InsecureChannelCredentials())),
    m_stub(vms::fs::FsService::NewStub(m_channel)),
    m_outbox_path(recording_storage::resolve_storage_base_path() / k_outbox_file_name),
    m_permanent_failures_path(recording_storage::resolve_storage_base_path() / k_permanent_failures_file_name)
{
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
  const std::string& file_path,
  std::chrono::system_clock::time_point start_ts,
  std::optional<std::chrono::system_clock::time_point> end_ts,
  std::uint64_t size_bytes)
{
  vms::fs::IngestClipRequest request;
  auto* clip = request.mutable_clip();
  clip->set_camera_id(camera_id);
  clip->set_file_path(file_path);
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

bool fs_client::ingest_clip(
  const recording& clip,
  std::string& clip_uid,
  std::string& error)
{
  vms::fs::IngestClipRequest request;
  auto* meta = request.mutable_clip();
  meta->set_camera_id(clip.camera_id);
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

bool fs_client::get_recordings(
  const std::string& camera_id,
  std::optional<std::chrono::system_clock::time_point> from_ts,
  std::optional<std::chrono::system_clock::time_point> to_ts,
  std::uint32_t limit,
  std::vector<recording>& result,
  std::string& error)
{
  vms::fs::GetRecordingsRequest request;
  request.set_camera_id(camera_id);
  request.set_limit(limit);
  if (from_ts)
  {
    *request.mutable_from_ts() = to_proto_ts(*from_ts);
  }
  if (to_ts)
  {
    *request.mutable_to_ts() = to_proto_ts(*to_ts);
  }

  vms::fs::GetRecordingsResponse response;
  grpc::ClientContext context;
  context.set_deadline(std::chrono::system_clock::now() + std::chrono::seconds(5));
  context.AddMetadata("dapr-app-id", m_app_id);

  const grpc::Status status = m_stub->GetRecordings(&context, request, &response);
  if (!status.ok())
  {
    error = status.error_message();
    logger::instance().error(std::format(
      "GetRecordings RPC via Dapr failed for app '{}': {}",
      m_app_id,
      error));
    return false;
  }

  result.clear();
  result.reserve(response.recordings_size());
  for (const auto& item : response.recordings())
  {
    result.push_back(recording{
      .camera_id = item.camera_id(),
      .file_path = item.file_path(),
      .start_ts = from_timestamp(item.start_ts()),
      .end_ts = item.has_end_ts()
        ? std::optional<std::chrono::system_clock::time_point>(from_timestamp(item.end_ts()))
        : std::nullopt,
      .size_bytes = item.size_bytes()
    });
  }

  return true;
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
