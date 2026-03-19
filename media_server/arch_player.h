#pragma once

#include <memory>
#include <string>
#include <vector>

#include "interfaces/reader.h"
#include "stream/jpeg_streamer.h"
#include "writer/webrtc_output_sink.h"


class arch_player {
public:
  arch_player(const std::string& camera_id,
    const std::string& client_id,
    const std::string& start_time,
    const std::string& storage_base_path,
    std::string& error);
  ~arch_player();
  arch_player(const arch_player&) = delete;
  arch_player& operator=(const arch_player&) = delete;
  arch_player(arch_player&&) noexcept = default;
  arch_player& operator=(arch_player&&) noexcept = default;

  bool set_state(const std::string& start_time,
                 std::string& error);


  const std::string& camera_id() const;

  void shutdown();

private:
  struct file_playback_config {
    std::string start_time;
    std::string storage_base_path;
  };

  bool recreate(const std::string& start_time,
                std::string& error);
  bool start_web_rtc(std::string& error);
  bool stop_web_rtc(std::string& error);

  std::string m_camera_id;
  std::string m_client_id;
  std::string m_storage_base_path;
  std::string m_start_time;
  std::unique_ptr<reader> m_reader;
  std::shared_ptr<webrtc_output_sink> m_webrtc_output_sink;
  mutable std::mutex m_sinks_mutex;
};
