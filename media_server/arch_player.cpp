#include "arch_player.h"

#include <thread>
#include <mutex>
#include <condition_variable>
#include <queue>
#include <functional>
#include <filesystem>

#include "reader/file_reader.h"
#include "interfaces/web_rtc_utils.h"

arch_player::arch_player(
  const std::string& client_id,
  const std::string& camera_id)
  : m_client_id(client_id), m_camera_id(camera_id), m_stop_worker(false)
{
  m_worker = std::thread([this] { this->worker_loop(); });
}

arch_player::~arch_player()
{
  {
    std::scoped_lock lock(m_task_mutex);
    m_stop_worker = true;
    m_task_cv.notify_all();
  }

  if (m_worker.joinable())
    m_worker.join();

  shutdown();
}

// --- очередь задач ---
void arch_player::post_task(std::function<void()> task)
{
  {
    std::scoped_lock lock(m_task_mutex);
    m_task_queue.push(std::move(task));
  }
  m_task_cv.notify_one();
}

void arch_player::worker_loop()
{
  while (true)
  {
    std::function<void()> task;
    {
      std::unique_lock lock(m_task_mutex);
      m_task_cv.wait(lock, [this] { return m_stop_worker || !m_task_queue.empty(); });
      if (m_stop_worker && m_task_queue.empty())
        break;
      task = std::move(m_task_queue.front());
      m_task_queue.pop();
    }
    task();
  }
}

// --- публичный интерфейс ---
bool arch_player::set_state(
  const std::chrono::system_clock::time_point& start_time,
  const std::string& camera_id)
{
  // кидаем задачу в очередь
  post_task([this, start_time, camera_id]()
    {
      m_camera_id = camera_id;
      shutdown();

      if (!open(start_time))
        return;

      if (!start_web_rtc())
        return;
    });

  return true; // сам вызов сразу возвращает, выполнение асинхронно
}

// --- приватные методы ---
bool arch_player::on_eof(const std::string& file)
{
  auto path = std::filesystem::path(file);
  auto ts = m_storage.parse_time(path.filename().string());
  if (!ts)
    return false;

  auto info = m_storage.find_next(m_camera_id, *ts);
  if (!info)
    return false;

  post_task([this, info]()
    {
      std::string error;
      if (!open_file_reader(info->file_path.string()))
      {
        return;
      }
      m_start_time = info->start_time;

      if (!start_web_rtc())
        return;
    });

  return true;
}

bool arch_player::open_file_reader(const std::string& file_path)
{
  auto playback_reader = std::make_shared<file_reader>(
    m_camera_id,
    [this](const std::string& f) { return this->on_eof(f); });

  {
    if (!playback_reader->open(file_path))
    {
      return false;
    }

    playback_reader->start();
    m_reader = playback_reader;
    m_cur_file_path = file_path;
  }

  return true;
}

bool arch_player::open(const std::chrono::system_clock::time_point& start_time)
{
  auto rec = m_storage.find_nearest(m_camera_id, start_time);
  if (!rec)
  {
    return false;
  }

  return open_file_reader(rec->file_path.string());
}

void arch_player::shutdown()
{
  std::shared_ptr<reader> reader_copy;
  {
    reader_copy = m_reader;
    m_reader.reset();
  }
  if (reader_copy)
    reader_copy->stop();
}

bool arch_player::start_web_rtc()
{
  std::shared_ptr<webrtc_output_sink> sink;
  {
    if (!m_webrtc_output_sink)
      m_webrtc_output_sink = std::make_shared<webrtc_output_sink>(resolve_default_webrtc_rtsp_url(m_client_id));
    sink = m_webrtc_output_sink;
  }

  if (m_reader)
    m_reader->add_sink(sink);
  else
  {
    return false;
  }

  return true;
}

bool arch_player::stop_web_rtc()
{
  std::shared_ptr<webrtc_output_sink> sink;
  {
    if (!m_webrtc_output_sink)
    {
      return false;
    }

    sink = m_webrtc_output_sink;
    m_webrtc_output_sink.reset();
  }

  if (m_reader && sink)
    m_reader->remove_sink(sink);

  return true;
}