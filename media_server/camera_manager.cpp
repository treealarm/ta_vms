#include "camera_manager.h"

bool camera_manager::add_camera(const std::string& camera_id,
                                const std::string& rtsp_url,
                                const std::string& storage_base_path,
                                bool recording,
                                bool jpeg_stream,
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

  auto existing = m_cameras.find(camera_id);
  if (existing != m_cameras.end()) {
    existing->second.camera->shutdown();
    m_cameras.erase(existing);
  }

  try {
    auto camera = std::make_unique<cam>(rtsp_url, camera_id, storage_base_path);
    if (recording) {
      camera->start_recording();
    }

    if (jpeg_stream) {
      if (!camera->start_jpeg_stream(error)) {
        return false;
      }
    }

    camera_entry entry;
    entry.camera = std::move(camera);
    entry.source = rtsp_url;
    entry.is_recording = recording;
    entry.is_jpeg_streaming = jpeg_stream;

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

  it->second.camera->start_recording();
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

  it->second.camera->stop_recording();
  it->second.is_recording = false;
  return true;
}

bool camera_manager::set_camera_state(const std::string& camera_id,
                                    bool recording,
                                    bool jpeg_stream,
                                    std::string& error) {
  std::scoped_lock lock(m_mutex);

  auto it = m_cameras.find(camera_id);
  if (it == m_cameras.end()) {
    error = "camera not found: " + camera_id;
    return false;
  }

  if (recording != it->second.is_recording) {
    if (recording) {
      it->second.camera->start_recording();
    } else {
      it->second.camera->stop_recording();
    }
    it->second.is_recording = recording;
  }

  if (jpeg_stream != it->second.is_jpeg_streaming) {
    if (jpeg_stream) {
      if (!it->second.camera->start_jpeg_stream(error)) {
        return false;
      }
    } else {
      if (!it->second.camera->stop_jpeg_stream(error)) {
        return false;
      }
    }
    it->second.is_jpeg_streaming = jpeg_stream;
  }

  return true;
}

std::vector<camera_manager::camera_info> camera_manager::list_cameras() const {
  std::scoped_lock lock(m_mutex);

  std::vector<camera_info> cameras;
  cameras.reserve(m_cameras.size());

  for (const auto& [camera_id, entry] : m_cameras) {
    cameras.push_back(camera_info{camera_id, entry.source, entry.is_recording});
  }

  return cameras;
}

void camera_manager::stop_all() {
  std::scoped_lock lock(m_mutex);

  for (auto& [_, entry] : m_cameras) {
    entry.camera->shutdown();
    entry.is_recording = false;
    entry.is_jpeg_streaming = false;
  }
}

bool camera_manager::start_play_file(const std::string& camera_id,
                                     const std::string& start_time,
                                     std::string& error) {
  if (camera_id.empty()) {
    error = "camera_id must not be empty";
    return false;
  }

  try {
    std::scoped_lock lock(m_mutex);

    auto existing = m_cameras.find(camera_id);
    if (existing != m_cameras.end()) {
      existing->second.camera->shutdown();
      m_cameras.erase(existing);
    }

    auto playback_camera = std::make_unique<cam>(
        camera_id, cam::file_playback_config{start_time, ""});

    camera_entry entry;
    entry.camera = std::move(playback_camera);
    entry.source = "records:" + start_time;
    entry.is_recording = false;
    entry.is_jpeg_streaming = false;

    m_cameras.emplace(camera_id, std::move(entry));
    return true;
  } catch (const std::exception& ex) {
    error = ex.what();
    return false;
  }
}

bool camera_manager::stop_play_file(const std::string& camera_id,
                                    std::string& error) {
  std::scoped_lock lock(m_mutex);

  auto it = m_cameras.find(camera_id);
  if (it == m_cameras.end()) {
    error = "camera not found: " + camera_id;
    return false;
  }

  it->second.camera->shutdown();
  m_cameras.erase(it);
  return true;
}
