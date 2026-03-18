#pragma once

#include <memory>
#include <string>
#include <vector>

#include "interfaces/reader.h"
#include "stream/jpeg_streamer.h"

class arch_player {
public:
  arch_player(const std::string& camera_id,
              const std::string& start_time,
              const std::string& storage_base_path,
              bool jpeg_stream,
              std::string& error);
  ~arch_player();
  arch_player(const arch_player&) = delete;
  arch_player& operator=(const arch_player&) = delete;
  arch_player(arch_player&&) noexcept = default;
  arch_player& operator=(arch_player&&) noexcept = default;

  bool set_state(const std::string& start_time,
                 bool jpeg_stream,
                 std::string& error);

  bool wait_next_jpeg_frame(std::vector<uint8_t>& frame,
                            uint64_t& frame_number,
                            std::string& error);

  const std::string& camera_id() const;

  void shutdown();

private:
  struct file_playback_config {
    std::string start_time;
    std::string storage_base_path;
  };

  bool recreate(const std::string& start_time,
                bool jpeg_stream,
                std::string& error);

  bool start_jpeg_stream(std::string& error);
  bool stop_jpeg_stream(std::string& error);

  std::string m_camera_id;
  std::string m_storage_base_path;
  std::string m_start_time;
  bool m_is_jpeg_streaming = false;
  bool m_is_running = false;
  std::unique_ptr<reader> m_reader;
  std::unique_ptr<jpeg_streamer> m_jpeg_streamer;
};
