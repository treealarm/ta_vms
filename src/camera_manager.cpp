#include "camera_manager.h"

bool camera_manager::add_camera(const std::string& camera_id,
                                const std::string& rtsp_url,
                                bool start_immediately,
                                std::string& error) {
  std::scoped_lock lock(m_mutex);

  if (camera_id.empty()) {
    error = "camera_id must not be empty";
    return false;
  }

  if (rtsp_url.empty()) {
    error = "rtsp_url must not be empty";
    return false;
  }

  if (m_cameras.contains(camera_id)) {
    error = "camera already exists: " + camera_id;
    return false;
  }

  try {
    auto camera = std::make_unique<cam>(rtsp_url, camera_id);
    if (start_immediately) {
      camera->start();
    }

    camera_entry entry;
    entry.camera = std::move(camera);
    entry.is_recording = start_immediately;

    m_cameras.emplace(camera_id, std::move(entry));
    return true;
  } catch (const std::exception& ex) {
    error = ex.what();
    return false;
  }
}

bool camera_manager::start_recording(const std::string& camera_id,
                                     std::string& error) {
  std::scoped_lock lock(m_mutex);

  auto it = m_cameras.find(camera_id);
  if (it == m_cameras.end()) {
    error = "camera not found: " + camera_id;
    return false;
  }

  if (it->second.is_recording) {
    error = "camera already recording: " + camera_id;
    return false;
  }

  it->second.camera->start();
  it->second.is_recording = true;
  return true;
}

bool camera_manager::stop_recording(const std::string& camera_id,
                                    std::string& error) {
  std::scoped_lock lock(m_mutex);

  auto it = m_cameras.find(camera_id);
  if (it == m_cameras.end()) {
    error = "camera not found: " + camera_id;
    return false;
  }

  if (!it->second.is_recording) {
    error = "camera is not recording: " + camera_id;
    return false;
  }

  it->second.camera->stop();
  it->second.is_recording = false;
  return true;
}

void camera_manager::stop_all() {
  std::scoped_lock lock(m_mutex);

  for (auto& [_, entry] : m_cameras) {
    if (entry.is_recording) {
      entry.camera->stop();
      entry.is_recording = false;
    }
  }
}
