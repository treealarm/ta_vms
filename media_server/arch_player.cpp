#include "arch_player.h"

#include <memory>
#include <stdexcept>

arch_player::arch_player(const std::string& camera_id,
                         const std::string& start_time,
                         const std::string& storage_base_path,
                         bool jpeg_stream,
                         std::string& error)
    : m_camera_id(camera_id),
      m_storage_base_path(storage_base_path) {
  if (!recreate(start_time, jpeg_stream, error)) {
    throw std::runtime_error(error);
  }
}

bool arch_player::recreate(const std::string& start_time,
                           bool jpeg_stream,
                           std::string& error) {
  auto playback_camera = std::make_unique<cam>(
      m_camera_id,
      cam::file_playback_config{start_time, m_storage_base_path});

  if (jpeg_stream && !playback_camera->start_jpeg_stream(error)) {
    return false;
  }

  m_camera = std::move(playback_camera);
  m_start_time = start_time;
  m_is_jpeg_streaming = jpeg_stream;
  return true;
}

bool arch_player::set_state(const std::string& start_time,
                            bool jpeg_stream,
                            std::string& error) {
  const bool has_new_start_time = !start_time.empty() && start_time != m_start_time;
  if (has_new_start_time) {
    if (m_camera) {
      m_camera->shutdown();
    }

    return recreate(start_time, jpeg_stream, error);
  }

  if (!m_camera) {
    error = "player is not initialized";
    return false;
  }

  if (jpeg_stream != m_is_jpeg_streaming) {
    if (jpeg_stream) {
      if (!m_camera->start_jpeg_stream(error)) {
        return false;
      }
    } else {
      if (!m_camera->stop_jpeg_stream(error)) {
        return false;
      }
    }

    m_is_jpeg_streaming = jpeg_stream;
  }

  return true;
}

bool arch_player::wait_next_jpeg_frame(std::vector<uint8_t>& frame,
                                       uint64_t& frame_number,
                                       std::string& error) {
  if (!m_camera) {
    error = "player is not initialized";
    return false;
  }

  return m_camera->wait_next_jpeg_frame(frame, frame_number, error);
}

const std::string& arch_player::camera_id() const {
  return m_camera_id;
}

void arch_player::shutdown() {
  if (!m_camera) {
    return;
  }

  m_camera->shutdown();
  m_is_jpeg_streaming = false;
}
