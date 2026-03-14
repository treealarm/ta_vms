#pragma once

#include <memory>
#include <string>
#include <vector>

#include "cam.h"

class arch_player {
public:
  arch_player(const std::string& camera_id,
              const std::string& start_time,
              const std::string& storage_base_path,
              bool jpeg_stream,
              std::string& error);

  bool set_state(const std::string& start_time,
                 bool jpeg_stream,
                 std::string& error);

  bool wait_next_jpeg_frame(std::vector<uint8_t>& frame,
                            uint64_t& frame_number,
                            std::string& error);

  const std::string& camera_id() const;

  void shutdown();

private:
  bool recreate(const std::string& start_time,
                bool jpeg_stream,
                std::string& error);

  std::string m_camera_id;
  std::string m_storage_base_path;
  std::string m_start_time;
  bool m_is_jpeg_streaming = false;
  std::unique_ptr<cam> m_camera;
};
