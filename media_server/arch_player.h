#pragma once

#include <memory>
#include <string>
#include <vector>
#include <optional>
#include <filesystem>

#include "interfaces/reader.h"
#include "stream/jpeg_streamer.h"
#include "writer/webrtc_output_sink.h"


class arch_player {
public:
  arch_player(
    const std::string& client_id,
    std::string& error);
  ~arch_player();
  arch_player(const arch_player&) = delete;
  arch_player& operator=(const arch_player&) = delete;
  arch_player(arch_player&&) noexcept = default;
  arch_player& operator=(arch_player&&) noexcept = default;

  bool set_state(
    const std::chrono::system_clock::time_point& start_time,
    const std::string& camera_id,
    std::string& error);


  const std::string& camera_id() const;
  std::chrono::system_clock::time_point start_time() const {
    return m_start_time;
  }
  void shutdown();

private:

  bool open(const std::chrono::system_clock::time_point& start_time,
                std::string& error);
  bool start_web_rtc(std::string& error);
  bool stop_web_rtc(std::string& error);

  std::optional<std::string> find_file(
    const std::string& camera_id,
    const std::filesystem::path& base_path,
    const std::chrono::system_clock::time_point& start_time);

  std::string m_camera_id;
  std::string m_client_id;
  std::chrono::system_clock::time_point m_start_time;
  std::unique_ptr<reader> m_reader;
  std::shared_ptr<webrtc_output_sink> m_webrtc_output_sink;
  mutable std::mutex m_sinks_mutex;
};
