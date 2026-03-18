#include "arch_player.h"

#include <cstdlib>
#include <memory>
#include <stdexcept>

#include "reader/file_reader.h"

namespace {
std::string resolve_storage_base_path(const std::string& storage_base_path)
{
  if (!storage_base_path.empty()) {
    return storage_base_path;
  }

  if (const char* value = std::getenv("VMS_STORAGE_BASE_PATH")) {
    if (value[0] != '\0') {
      return value;
    }
  }

  return "records";
}
}

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

arch_player::~arch_player() {
  shutdown();
}

bool arch_player::recreate(const std::string& start_time,
                           bool jpeg_stream,
                           std::string& error) {
  const auto playback_config = file_playback_config{start_time, m_storage_base_path};
  auto playback_reader = std::make_unique<file_reader>(
      m_camera_id,
      resolve_storage_base_path(playback_config.storage_base_path));

  if (!playback_reader->open(playback_config.start_time)) {
    error = "Failed to open media source";
    return false;
  }

  playback_reader->start();

  if (jpeg_stream) {
    auto streamer = std::make_unique<jpeg_streamer>();
    if (!streamer->init(playback_reader->video_stream(), error)) {
      playback_reader->stop();
      return false;
    }

    playback_reader->add_sink(streamer.get());
    m_jpeg_streamer = std::move(streamer);
  }

  m_reader = std::move(playback_reader);
  m_start_time = start_time;
  m_is_jpeg_streaming = jpeg_stream;
  m_is_running = true;
  return true;
}

bool arch_player::set_state(const std::string& start_time,
                            bool jpeg_stream,
                            std::string& error) {
  const bool has_new_start_time = !start_time.empty() && start_time != m_start_time;
  if (has_new_start_time) {
    shutdown();
    return recreate(start_time, jpeg_stream, error);
  }

  if (!m_reader) {
    error = "player is not initialized";
    return false;
  }

  if (jpeg_stream != m_is_jpeg_streaming) {
    const bool success = jpeg_stream
      ? start_jpeg_stream(error)
      : stop_jpeg_stream(error);

    if (!success) {
      return false;
    }

    m_is_jpeg_streaming = jpeg_stream;
  }

  return true;
}

bool arch_player::wait_next_jpeg_frame(std::vector<uint8_t>& frame,
                                       uint64_t& frame_number,
                                       std::string& error) {
  if (!m_reader) {
    error = "player is not initialized";
    return false;
  }

  if (!m_jpeg_streamer) {
    error = "jpeg stream is not running";
    return false;
  }

  if (!m_jpeg_streamer->wait_next_frame(frame, frame_number)) {
    error = "jpeg stream closed";
    return false;
  }

  return true;
}

const std::string& arch_player::camera_id() const {
  return m_camera_id;
}

void arch_player::shutdown() {
  if (!m_reader) {
    return;
  }

  if (m_jpeg_streamer) {
    m_reader->remove_sink(m_jpeg_streamer.get());
    m_jpeg_streamer.reset();
  }

  if (m_is_running) {
    m_reader->stop();
    m_is_running = false;
  }

  m_is_jpeg_streaming = false;
  m_reader.reset();
}

bool arch_player::start_jpeg_stream(std::string& error) {
  if (m_jpeg_streamer) {
    error = "jpeg stream already running";
    return false;
  }

  auto streamer = std::make_unique<jpeg_streamer>();
  if (!streamer->init(m_reader->video_stream(), error)) {
    return false;
  }

  m_reader->add_sink(streamer.get());
  m_jpeg_streamer = std::move(streamer);

  return true;
}

bool arch_player::stop_jpeg_stream(std::string& error) {
  if (!m_jpeg_streamer) {
    error = "jpeg stream is not running";
    return false;
  }

  m_reader->remove_sink(m_jpeg_streamer.get());
  m_jpeg_streamer.reset();

  return true;
}
