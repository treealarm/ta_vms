#include "camera_manager.h"

#include <algorithm>
#include <chrono>
#include <cmath>
#include <filesystem>
#include <sstream>
#include <iomanip>
#include <ctime>
#include <optional>
#include <vector>

namespace {
std::optional<std::chrono::system_clock::time_point> parse_segment_start_time(
    const std::string& file_name) {
  if (file_name.size() < 19) {
    return std::nullopt;
  }

  std::tm tm{};
  std::istringstream stream(file_name.substr(0, 19));
  stream >> std::get_time(&tm, "%Y-%m-%d_%H-%M-%S");
  if (stream.fail()) {
    return std::nullopt;
  }

  const std::time_t value = std::mktime(&tm);
  if (value < 0) {
    return std::nullopt;
  }

  return std::chrono::system_clock::from_time_t(value);
}

std::optional<std::chrono::system_clock::time_point> parse_requested_time(
    const std::string& value) {
  if (value.empty()) {
    return std::nullopt;
  }

  std::tm tm{};
  std::istringstream stream(value);
  stream >> std::get_time(&tm, "%Y-%m-%dT%H:%M:%S");
  if (stream.fail()) {
    return std::nullopt;
  }

  const std::time_t parsed_time = std::mktime(&tm);
  if (parsed_time < 0) {
    return std::nullopt;
  }

  return std::chrono::system_clock::from_time_t(parsed_time);
}
}  // namespace

bool camera_manager::add_camera(const std::string& camera_id,
                                const std::string& rtsp_url,
                                const std::string& storage_base_path,
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

  auto existing = m_cameras.find(camera_id);
  if (existing != m_cameras.end()) {
    existing->second.camera->shutdown();
    m_cameras.erase(existing);
  }

  try {
    auto camera = std::make_unique<cam>(rtsp_url, camera_id, storage_base_path);
    if (start_immediately) {
      camera->start_recording();
    }

    camera_entry entry;
    entry.camera = std::move(camera);
    entry.source = rtsp_url;
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

bool camera_manager::start_jpeg_stream(const std::string& camera_id,
                                       std::string& error) {
  std::scoped_lock lock(m_mutex);

  auto it = m_cameras.find(camera_id);
  if (it == m_cameras.end()) {
    error = "camera not found: " + camera_id;
    return false;
  }

  return it->second.camera->start_jpeg_stream(error);
}

bool camera_manager::wait_next_jpeg_frame(const std::string& camera_id,
                                          std::vector<uint8_t>& frame,
                                          uint64_t& frame_number,
                                          std::string& error) {
  std::unique_lock lock(m_mutex);

  auto it = m_cameras.find(camera_id);
  if (it == m_cameras.end()) {
    error = "camera not found: " + camera_id;
    return false;
  }

  auto* camera = it->second.camera.get();
  lock.unlock();

  return camera->wait_next_jpeg_frame(frame, frame_number, error);
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

bool camera_manager::stop_jpeg_stream(const std::string& camera_id,
                                      std::string& error) {
  std::scoped_lock lock(m_mutex);

  auto it = m_cameras.find(camera_id);
  if (it == m_cameras.end()) {
    error = "camera not found: " + camera_id;
    return false;
  }

  return it->second.camera->stop_jpeg_stream(error);
}

void camera_manager::stop_all() {
  std::scoped_lock lock(m_mutex);

  for (auto& [_, entry] : m_cameras) {
    entry.camera->shutdown();
    entry.is_recording = false;
  }
}

bool camera_manager::start_play_file(const std::string& camera_id,
                                     const std::string& start_time,
                                     std::string& error) {
  if (camera_id.empty()) {
    error = "camera_id must not be empty";
    return false;
  }

  const auto requested_time = parse_requested_time(start_time);
  if (!requested_time) {
    error = "start_time must be in format YYYY-MM-DDTHH:MM:SS";
    return false;
  }

  std::vector<std::filesystem::path> candidate_files;
  const std::filesystem::path camera_directory = std::filesystem::path("records") / camera_id;

  if (!std::filesystem::exists(camera_directory)) {
    error = "records not found for camera: " + camera_id;
    return false;
  }

  for (const auto& entry : std::filesystem::recursive_directory_iterator(camera_directory)) {
    if (!entry.is_regular_file()) {
      continue;
    }

    if (entry.path().extension() != ".mkv") {
      continue;
    }

    if (parse_segment_start_time(entry.path().filename().string())) {
      candidate_files.push_back(entry.path());
    }
  }

  if (candidate_files.empty()) {
    error = "no playable files found for camera: " + camera_id;
    return false;
  }

  std::sort(candidate_files.begin(), candidate_files.end());

  size_t best_index = 0;
  auto best_difference = std::chrono::seconds::max();

  for (size_t i = 0; i < candidate_files.size(); ++i) {
    const auto parsed_time = parse_segment_start_time(candidate_files[i].filename().string());
    if (!parsed_time) {
      continue;
    }

    const auto difference = *parsed_time > *requested_time ? *parsed_time - *requested_time : *requested_time - *parsed_time;
    if (difference < best_difference) {
      best_difference = difference;
      best_index = i;
    }
  }

  std::vector<std::string> playlist;
  playlist.reserve(candidate_files.size() - best_index);
  for (size_t i = best_index; i < candidate_files.size(); ++i) {
    playlist.push_back(candidate_files[i].string());
  }

  return add_camera(camera_id, "playlist:" + cam::join_playlist(playlist), "", false, error);
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
