#include "cam.h"

#include <cstdlib>
#include <stdexcept>

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

bool is_rtsp_source(const std::string& source)
{
  return source.rfind("rtsp://", 0) == 0 || source.rfind("rtsps://", 0) == 0;
}
}

cam::cam(const std::string& source,
         const std::string& cam_id,
         const std::string& storage_base_path)
    : m_source(source),
      m_cam_id(cam_id),
      m_writer(resolve_storage_base_path(storage_base_path), cam_id, 60) {
  if (!is_rtsp_source(m_source)) {
    throw std::runtime_error("cam supports only RTSP sources");
  }

  m_reader = std::make_unique<rtsp_reader>();
  if (!m_reader->open(m_source)) {
    throw std::runtime_error("Failed to open media source");
  }

  m_reader->start();
  m_is_running = true;
}

cam::~cam() {
  shutdown();
}

void cam::start_recording() {
  {
    std::scoped_lock lock(m_sinks_mutex);
    if (m_is_recording) {
      return;
    }

    m_is_recording = true;
    m_is_writer_initialized = false;
  }

  update_reader_subscription();
}

void cam::stop_recording() {
  {
    std::scoped_lock lock(m_sinks_mutex);
    if (!m_is_recording) {
      return;
    }

    m_is_recording = false;
    m_is_writer_initialized = false;
    m_writer.close();
  }

  update_reader_subscription();
}

void cam::shutdown() {
  {
    std::scoped_lock lock(m_sinks_mutex);
    if (m_jpeg_streamer) {
      m_jpeg_streamer->close();
      m_jpeg_streamer.reset();
    }

    if (m_is_recording) {
      m_is_recording = false;
      m_is_writer_initialized = false;
      m_writer.close();
    }
  }

  update_reader_subscription();

  if (m_is_running) {
    m_reader->stop();
    m_is_running = false;
  }
}

bool cam::start_jpeg_stream(std::string& error) {
  {
    std::scoped_lock lock(m_sinks_mutex);

    if (m_jpeg_streamer) {
      error = "jpeg stream already running";
      return false;
    }

    auto* stream = m_reader->video_stream();
    if (!stream) {
      error = "video stream is not available yet";
      return false;
    }

    auto streamer = std::make_unique<jpeg_streamer>();
    if (!streamer->init(stream, error)) {
      return false;
    }

    m_jpeg_streamer = std::move(streamer);
  }

  update_reader_subscription();
  return true;
}

bool cam::wait_next_jpeg_frame(std::vector<uint8_t>& frame,
                               uint64_t& frame_number,
                               std::string& error) {
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
  {
    std::scoped_lock lock(m_sinks_mutex);

    if (!m_jpeg_streamer) {
      error = "jpeg stream is not running";
      return false;
    }

    m_jpeg_streamer->close();
    m_jpeg_streamer.reset();
  }

  update_reader_subscription();
  return true;
}

void cam::on_packet(const std::shared_ptr<media_packet>& pkt) {
  std::scoped_lock lock(m_sinks_mutex);

  if (m_is_recording) {
    if (!m_is_writer_initialized) {
      auto* stream = m_reader->video_stream();
      if (stream && m_writer.init(stream)) {
        m_is_writer_initialized = true;
      }
    }

    if (m_is_writer_initialized) {
      auto writer_packet = clone_media_packet(pkt);
      if (writer_packet) {
        m_writer.on_packet(writer_packet);
      }
    }
  }

  if (m_jpeg_streamer) {
    auto jpeg_packet = clone_media_packet(pkt);
    if (jpeg_packet) {
      m_jpeg_streamer->on_packet(jpeg_packet);
    }
  }
}

void cam::update_reader_subscription() {
  std::scoped_lock lock(m_sinks_mutex);

  const bool has_consumers = m_is_recording || static_cast<bool>(m_jpeg_streamer);

  if (has_consumers && !m_is_attached_to_reader) {
    m_reader->add_sink(this);
    m_is_attached_to_reader = true;
    return;
  }

  if (!has_consumers && m_is_attached_to_reader) {
    m_reader->remove_sink(this);
    m_is_attached_to_reader = false;
  }
}
