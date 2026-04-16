#pragma once

#include <memory>
#include <string>
#include <vector>
#include <optional>
#include <filesystem>
#include <queue>
#include <optional>

#include "interfaces/reader.h"
#include "stream/jpeg_streamer.h"
#include "writer/webrtc_output_sink.h"
#include "file_system/recording_storage.h"

struct player_command
{
  enum class type
  {
    play,
    pause,
    stop,
    step
  };

  type cmd;

  // параметры (используешь по ситуации)
  std::optional<int32_t> step_frames;
  std::optional<int64_t> position_ms;
  std::optional<double> speed;
};

class arch_player {
public:
  arch_player(
    const std::string& client_id,
    const std::string& camera_id);
  ~arch_player();
  arch_player(const arch_player&) = delete;
  arch_player& operator=(const arch_player&) = delete;
  arch_player(arch_player&&) noexcept = default;
  arch_player& operator=(arch_player&&) noexcept = default;

  bool set_state(
    const std::chrono::system_clock::time_point& start_time,
    const std::string& camera_id,
    std::optional<std::chrono::milliseconds> position);

  bool set_player_command(const player_command& cmd);

  const std::string& camera_id() const
  {
    std::scoped_lock lock(m_data_mutex);
    return m_camera_id;
  };
  std::optional<std::string> file_path() const
  {
    std::scoped_lock lock(m_data_mutex);
    return m_cur_file_path;
  };
  std::chrono::system_clock::time_point cur_time() const 
  {
    std::scoped_lock lock(m_data_mutex);
    return m_cur_time;
  }
  reader_progress get_progress() const 
  {
    std::scoped_lock lock(m_data_mutex);
    return m_progress;
  }
  std::chrono::system_clock::time_point get_start_time() const
  {
    std::scoped_lock lock(m_data_mutex);
    return m_start_time;
  }
  player_mode get_mode() const
  {
    return m_mode;
  }
  void shutdown();

private:
 
  bool open_file_reader(const std::string& file_path, std::chrono::milliseconds position);

  bool start_web_rtc(std::shared_ptr<reader> reader);
  bool stop_web_rtc(std::shared_ptr<reader> reader);

  void set_player_mode();
  void step(int32_t steps);

  bool on_eof(const std::string& file);
  void on_progress(const reader_progress& progress);

  void worker_loop();
  void post_task(std::function<void()> task);

  std::string m_client_id;
  std::string m_camera_id;
  reader_progress m_progress;
  std::optional<std::string> m_cur_file_path;

  mutable std::mutex m_data_mutex;
  
  std::chrono::system_clock::time_point m_cur_time;
  std::chrono::system_clock::time_point m_start_time;
  std::shared_ptr<reader> m_reader;

  std::shared_ptr<webrtc_output_sink> m_webrtc_output_sink;

  recording_storage m_storage;

  std::queue<std::function<void()>> m_task_queue;
  mutable std::mutex m_task_mutex;
  std::condition_variable m_task_cv;
  bool m_stop_worker;
  std::thread m_worker;

  std::atomic<player_mode> m_mode{ player_mode::undefined };
  std::atomic<double> m_play_speed = 1.0f;
};
