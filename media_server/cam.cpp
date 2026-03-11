#include "cam.h"

#include <cstdlib>
#include <stdexcept>

#include "reader/file_reader.h"
#include "reader/rtsp_reader.h"

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

bool is_rtsp_source(const std::string& source) {
  return source.rfind("rtsp://", 0) == 0 || source.rfind("rtsps://", 0) == 0;
}

bool is_file_source(const std::string& source) {
  return source.rfind("playlist:", 0) == 0 || source.rfind("file:", 0) == 0 || !is_rtsp_source(source);
}
}

cam::cam(const std::string& source,
         const std::string& cam_id,
         const std::string& storage_base_path)
    : m_source(source),
      m_cam_id(cam_id),
      m_writer(resolve_storage_base_path(storage_base_path), cam_id, 60) {
  if (is_file_source(m_source)) {
    m_reader = std::make_unique<file_reader>();
  } else {
    m_reader = std::make_unique<rtsp_reader>();
  }

  if (!m_reader->open(m_source)) {
    throw std::runtime_error("Failed to open media source");
  }

  m_writer.init(m_reader->video_stream());

  m_reader->start();
  m_is_running = true;
}

cam::cam(const std::string& cam_id,
         const file_playback_config& playback_config)
    : m_source("records:" + playback_config.start_time),
      m_cam_id(cam_id),
      m_writer(resolve_storage_base_path(playback_config.storage_base_path), cam_id, 60) {
  auto reader = std::make_unique<file_reader>(cam_id, resolve_storage_base_path(playback_config.storage_base_path));
  if (!reader->open(playback_config.start_time)) {
    throw std::runtime_error("Failed to open media source");
  }

  m_reader = std::move(reader);

  m_writer.init(m_reader->video_stream());

  m_reader->start();
  m_is_running = true;
}

cam::~cam() {
  shutdown();
}

std::string cam::join_playlist(const std::vector<std::string>& files) {
  std::string joined;
  for (size_t i = 0; i < files.size(); ++i) {
    if (i > 0) {
      joined += '|';
    }

    joined += files[i];
  }

  return joined;
}

void cam::start_recording() {
  if (m_is_recording) {
    return;
  }

  m_reader->add_sink(&m_writer);
  m_is_recording = true;
}

void cam::stop_recording() {
  if (!m_is_recording) {
    return;
  }

  m_reader->remove_sink(&m_writer);
  m_writer.close();
  m_is_recording = false;
}

void cam::shutdown() {
  if (m_jpeg_streamer) {
    m_reader->remove_sink(m_jpeg_streamer.get());
    m_jpeg_streamer->close();
    m_jpeg_streamer.reset();
  }

  stop_recording();

  if (m_is_running) {
    m_reader->stop();
    m_is_running = false;
  }
}

bool cam::start_jpeg_stream(std::string& error) {
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

bool cam::wait_next_jpeg_frame(std::vector<uint8_t>& frame, uint64_t& frame_number, std::string& error) {
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

bool cam::stop_jpeg_stream(std::string& error) {
  if (!m_jpeg_streamer) {
    error = "jpeg stream is not running";
    return false;
  }

  m_reader->remove_sink(m_jpeg_streamer.get());
  m_jpeg_streamer->close();
  m_jpeg_streamer.reset();

  return true;
}
