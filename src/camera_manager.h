#pragma once

#include <map>
#include <memory>
#include <mutex>
#include <string>
#include <vector>

#include "cam.h"

class camera_manager {
public:
  bool add_camera(const std::string& camera_id,
                  const std::string& rtsp_url,
                  bool start_immediately,
                  std::string& error);

  bool start_recording(const std::string& camera_id, std::string& error);
  bool stop_recording(const std::string& camera_id, std::string& error);
  bool start_jpeg_stream(const std::string& camera_id, std::string& error);
  bool stop_jpeg_stream(const std::string& camera_id, std::string& error);
  bool wait_next_jpeg_frame(const std::string& camera_id,
                            std::vector<uint8_t>& frame,
                            uint64_t& frame_number,
                            std::string& error);

  void stop_all();

private:
  struct camera_entry {
    std::unique_ptr<cam> camera;
    bool is_recording = false;
  };

  std::mutex m_mutex;
  std::map<std::string, camera_entry> m_cameras;
};
