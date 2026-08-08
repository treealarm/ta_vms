#pragma once

#include <atomic>
#include <chrono>
#include <cstdint>
#include <deque>
#include <memory>
#include <mutex>
#include <optional>
#include <string>
#include <thread>
#include <vector>

#include "edge_import/edge_import_worker.h"

enum class edge_import_state
{
  running,
  completed,
  failed,
  cancelled
};

struct edge_import_status
{
  std::string job_id;
  std::string camera_id;
  std::string stream_name;
  edge_import_state state = edge_import_state::running;
  std::string message;
  std::chrono::system_clock::time_point start_ts{};
  std::chrono::system_clock::time_point end_ts{};
  std::chrono::system_clock::time_point position{};
  uint32_t clips_written = 0;
  uint64_t bytes_written = 0;
  std::string export_path;
};

/// Tracks camera-side archive imports. One import runs per camera at a time so a pull from an SD
/// card cannot starve the live pipeline for that device, and because cameras have very few RTSP
/// sessions to give: a F-IC-2642C2MSZ4 with a live stream running answers a second replay session
/// with "453 Not Enough Bandwidth". A job that hits that retries with backoff — live video always
/// wins over a background pull. Finished jobs stay in memory long enough for the UI to read the
/// final status.
class edge_import_manager
{
public:
  static edge_import_manager& instance();

  edge_import_manager(const edge_import_manager&) = delete;
  edge_import_manager& operator=(const edge_import_manager&) = delete;

  /// Returns the accepted job, or std::nullopt with `error` set when the request is invalid or the
  /// camera already has an import in flight. `settings.job_id` is filled in when empty.
  std::optional<edge_import_status> start(edge_import_settings settings, std::string& error);

  [[nodiscard]] std::optional<edge_import_status> get(const std::string& job_id) const;
  /// Returns false when the job id is unknown or the job already finished.
  bool cancel(const std::string& job_id);
  [[nodiscard]] std::vector<edge_import_status> list(const std::string& camera_id) const;

private:
  edge_import_manager() = default;
  ~edge_import_manager();

  struct job
  {
    edge_import_status status;
    std::atomic_bool cancel{ false };
    std::thread worker;
  };

  void run_job(const std::shared_ptr<job>& entry, edge_import_settings settings);
  [[nodiscard]] bool camera_busy_locked(const std::string& camera_id) const;
  void prune_finished_locked();

  static constexpr std::size_t k_max_remembered_jobs = 64;
  // The camera refuses extra RTSP sessions while it is busy; back off rather than give up, but
  // stay bounded so a permanently busy device does not pin a thread forever.
  static constexpr int k_max_busy_retries = 5;
  static constexpr std::chrono::seconds k_busy_retry_base{ 5 };

  mutable std::mutex m_mutex;
  std::deque<std::shared_ptr<job>> m_jobs;
};
