#pragma once

#include <cstddef>
#include <chrono>
#include <condition_variable>
#include <filesystem>
#include <memory>
#include <mutex>
#include <optional>
#include <queue>
#include <string>
#include <thread>
#include <unordered_map>
#include <unordered_set>
#include <vector>

#include <grpcpp/grpcpp.h>

#include "vms_fs.grpc.pb.h"

class fs_client
{
public:
  static fs_client& instance();
  // Must be called once from main() before instance() is first accessed.
  static void set_fs_app_id(const std::string& id);

  fs_client(const fs_client&) = delete;
  fs_client& operator=(const fs_client&) = delete;

  struct recording
  {
    std::string camera_id;
    std::string stream_name;
    std::string file_path;
    std::chrono::system_clock::time_point start_ts;
    std::optional<std::chrono::system_clock::time_point> end_ts;
    std::uint64_t size_bytes = 0;
  };

  struct storage_info
  {
    std::string storage_id;
    std::string root_path;
    bool empty() const { return root_path.empty(); }
  };

  // Returns all storages assigned to the camera in priority order (lowest priority number first).
  // Falls back gracefully: returns empty vector if no storages are assigned or VmsFs is unavailable.
  /// Drops every cached storage answer. Called when VmsCfg says a storage changed — taken out of
  /// service, repointed, removed. Without it the entry lives out its TTL, and a disk disabled for
  /// writing keeps receiving segments for as long as that lasts.
  void invalidate_storage_cache();

  std::vector<storage_info> get_camera_storages(const std::string& camera_id);

  // force_retained is set by camera-side (edge) import: the operator asked for that range
  // explicitly, so VmsFs must keep the clip regardless of the camera's recording policy.
  void enqueue_ingest_clip(
    const std::string& camera_id,
    const std::string& stream_name,
    const std::string& storage_id,
    const std::string& file_path,
    std::chrono::system_clock::time_point start_ts,
    std::optional<std::chrono::system_clock::time_point> end_ts,
    std::uint64_t size_bytes,
    bool force_retained = false,
    const std::string& origin = {});

  struct liveness_entry
  {
    std::string camera_id;
    std::string stream_name;
    bool alive = false;
    std::optional<std::chrono::system_clock::time_point> last_frame_ts;
    std::optional<std::chrono::system_clock::time_point> recovered_frame_ts;
    bool recording_expected = false;
  };

  // Fire-and-forget: a lost report is corrected by the next one, so no outbox is involved. Called
  // from the reporting timer, never from a reader thread — the RPC has a deadline and would stall
  // the read loop.
  void report_source_liveness(
    const std::string& server_id,
    const std::string& instance_id,
    const std::vector<liveness_entry>& streams);

  bool ingest_clip(
    const recording& clip,
    std::string& clip_uid,
    std::string& error);

private:
  fs_client();
  ~fs_client();

  enum class ingest_result
  {
    success,
    retryable_failure,
    permanent_failure
  };

  struct ingest_outcome
  {
    ingest_result result = ingest_result::success;
    std::string reason;
  };

  void worker_loop();
  ingest_outcome ingest_clip_sync(const vms::fs::IngestClipRequest& request);
  bool load_outbox_chunk_locked(std::size_t max_records);
  bool append_outbox_locked(const vms::fs::IngestClipRequest& request) const;
  bool append_permanent_failure_locked(
    const vms::fs::IngestClipRequest& request,
    const std::string& reason) const;

  std::vector<storage_info> fetch_camera_storages(const std::string& camera_id);
  void trigger_storage_refresh(const std::string& camera_id);

  struct storage_cache_entry
  {
    std::vector<storage_info> storages;  // all storages in priority order
    std::chrono::steady_clock::time_point fetched_at;
  };

  std::string m_app_id;
  std::shared_ptr<grpc::Channel> m_channel;
  std::unique_ptr<vms::fs::FsService::Stub> m_stub;
  std::filesystem::path m_outbox_path;
  std::filesystem::path m_permanent_failures_path;
  std::mutex m_mutex;
  mutable std::mutex m_file_mutex;
  std::condition_variable m_cv;
  std::queue<vms::fs::IngestClipRequest> m_queue;
  std::thread m_worker;
  bool m_stopping = false;

  mutable std::mutex m_storage_mutex;
  std::unordered_map<std::string, storage_cache_entry> m_storage_cache;
  std::unordered_set<std::string> m_storage_refreshing;
};
