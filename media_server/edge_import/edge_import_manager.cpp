#include "edge_import/edge_import_manager.h"

#include <algorithm>
#include <format>
#include <random>
#include <utility>

#include "logger/logger.h"

namespace {

std::string make_job_id()
{
  static std::mt19937_64 rng{ std::random_device{}() };
  static std::mutex rng_mutex;
  uint64_t high = 0;
  uint64_t low = 0;
  {
    const std::lock_guard<std::mutex> lock(rng_mutex);
    high = rng();
    low = rng();
  }
  return std::format("{:016x}{:016x}", high, low);
}

}  // namespace

edge_import_manager& edge_import_manager::instance()
{
  static edge_import_manager manager;
  return manager;
}

edge_import_manager::~edge_import_manager()
{
  std::deque<std::shared_ptr<job>> jobs;
  {
    const std::lock_guard<std::mutex> lock(m_mutex);
    jobs = m_jobs;
  }
  for (const auto& entry : jobs)
  {
    entry->cancel.store(true);
  }
  for (const auto& entry : jobs)
  {
    if (entry->worker.joinable())
    {
      entry->worker.join();
    }
  }
}

std::optional<edge_import_status> edge_import_manager::start(
  edge_import_settings settings,
  std::string& error)
{
  if (settings.camera_id.empty() || settings.stream_name.empty())
  {
    error = "camera_id and stream_name are required";
    return std::nullopt;
  }
  if (settings.replay_uri.empty())
  {
    error = "replay_uri is required";
    return std::nullopt;
  }
  if (settings.end_ts <= settings.start_ts)
  {
    error = "end must be after start";
    return std::nullopt;
  }
  if (settings.recording_start_ts > settings.start_ts)
  {
    error = "recording_start must be at or before start";
    return std::nullopt;
  }
  if (settings.job_id.empty())
  {
    settings.job_id = make_job_id();
  }

  auto entry = std::make_shared<job>();
  entry->status.job_id = settings.job_id;
  entry->status.camera_id = settings.camera_id;
  entry->status.stream_name = settings.stream_name;
  entry->status.state = edge_import_state::running;
  entry->status.start_ts = settings.start_ts;
  entry->status.end_ts = settings.end_ts;
  entry->status.position = settings.start_ts;

  edge_import_status snapshot;
  {
    const std::lock_guard<std::mutex> lock(m_mutex);
    if (camera_busy_locked(settings.camera_id))
    {
      error = std::format("camera '{}' already has an import in progress", settings.camera_id);
      return std::nullopt;
    }
    prune_finished_locked();
    m_jobs.push_back(entry);

    // Started under the same lock that published the job: a thread assigned afterwards races with
    // prune_finished_locked() reading entry->worker, and a throwing std::thread constructor would
    // strand the job in `running` forever, blocking every later import for this camera.
    try
    {
      entry->worker = std::thread(
        [this, entry, settings]() mutable { run_job(entry, std::move(settings)); });
    }
    catch (const std::exception& ex)
    {
      entry->status.state = edge_import_state::failed;
      entry->status.message = std::format("could not start import thread: {}", ex.what());
      error = entry->status.message;
      return std::nullopt;
    }
    snapshot = entry->status;
  }

  logger::instance().info(std::format(
    "edge import: started job='{}' camera='{}' stream='{}' export={}",
    snapshot.job_id, snapshot.camera_id, snapshot.stream_name, settings.export_to_file));

  return snapshot;
}

void edge_import_manager::run_job(
  const std::shared_ptr<job>& entry,
  edge_import_settings settings)
{
  edge_import_outcome outcome;

  for (int attempt = 0; attempt <= k_max_busy_retries; ++attempt)
  {
    if (entry->cancel.load())
    {
      outcome.cancelled = true;
      break;
    }

    edge_import_worker worker(
      settings,
      entry->cancel,
      [this, entry](const edge_import_progress& progress)
      {
        const std::lock_guard<std::mutex> lock(m_mutex);
        entry->status.position = progress.position;
        entry->status.clips_written = progress.clips_written;
        entry->status.bytes_written = progress.bytes_written;
        entry->status.export_path = progress.export_path;
      });

    outcome = worker.run();
    // Only an explicit "the device refused another session" is retried. Matching the message text
    // would also catch our own progress wording and re-run a finished import over its own files.
    if (outcome.success || outcome.cancelled || !outcome.device_busy)
    {
      break;
    }
    if (attempt == k_max_busy_retries)
    {
      break;
    }

    const auto wait = k_busy_retry_base * (1 << attempt);
    logger::instance().warning(std::format(
      "edge import: camera '{}' busy ({}), retrying in {}s (attempt {}/{})",
      settings.camera_id, outcome.message, wait.count(), attempt + 1, k_max_busy_retries));

    // Sleep in short slices so a cancel during the backoff is honoured promptly.
    for (auto slept = std::chrono::seconds::zero(); slept < wait; slept += std::chrono::seconds(1))
    {
      if (entry->cancel.load()) break;
      std::this_thread::sleep_for(std::chrono::seconds(1));
    }
  }

  const std::lock_guard<std::mutex> lock(m_mutex);
  entry->status.position = outcome.progress.position;
  entry->status.clips_written = outcome.progress.clips_written;
  entry->status.bytes_written = outcome.progress.bytes_written;
  entry->status.export_path = outcome.progress.export_path;
  entry->status.message = outcome.message;
  if (outcome.cancelled)
  {
    entry->status.state = edge_import_state::cancelled;
  }
  else if (outcome.success)
  {
    entry->status.state = edge_import_state::completed;
  }
  else
  {
    entry->status.state = edge_import_state::failed;
  }

  logger::instance().info(std::format(
    "edge import: finished job='{}' camera='{}' clips={} message='{}'",
    entry->status.job_id, entry->status.camera_id,
    entry->status.clips_written, entry->status.message));
}

std::optional<edge_import_status> edge_import_manager::get(const std::string& job_id) const
{
  const std::lock_guard<std::mutex> lock(m_mutex);
  for (const auto& entry : m_jobs)
  {
    if (entry->status.job_id == job_id)
    {
      return entry->status;
    }
  }
  return std::nullopt;
}

bool edge_import_manager::cancel(const std::string& job_id)
{
  const std::lock_guard<std::mutex> lock(m_mutex);
  for (const auto& entry : m_jobs)
  {
    if (entry->status.job_id != job_id)
    {
      continue;
    }
    if (entry->status.state != edge_import_state::running)
    {
      return false;
    }
    entry->cancel.store(true);
    return true;
  }
  return false;
}

std::vector<edge_import_status> edge_import_manager::list(const std::string& camera_id) const
{
  const std::lock_guard<std::mutex> lock(m_mutex);
  std::vector<edge_import_status> out;
  out.reserve(m_jobs.size());
  for (const auto& entry : m_jobs)
  {
    if (!camera_id.empty() && entry->status.camera_id != camera_id)
    {
      continue;
    }
    out.push_back(entry->status);
  }
  return out;
}

bool edge_import_manager::camera_busy_locked(const std::string& camera_id) const
{
  return std::ranges::any_of(
    m_jobs,
    [&camera_id](const std::shared_ptr<job>& entry)
    {
      return entry->status.camera_id == camera_id &&
             entry->status.state == edge_import_state::running;
    });
}

void edge_import_manager::prune_finished_locked()
{
  while (m_jobs.size() >= k_max_remembered_jobs)
  {
    const auto oldest = std::find_if(
      m_jobs.begin(), m_jobs.end(),
      [](const std::shared_ptr<job>& entry)
      { return entry->status.state != edge_import_state::running; });
    if (oldest == m_jobs.end())
    {
      return;
    }
    if ((*oldest)->worker.joinable())
    {
      (*oldest)->worker.join();
    }
    m_jobs.erase(oldest);
  }
}
