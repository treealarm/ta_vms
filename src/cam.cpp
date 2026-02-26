#include "cam.h"

#include <stdexcept>

cam::cam(const std::string& rtsp_url, const std::string& cam_id)
    : m_rtsp_url(rtsp_url), m_cam_id(cam_id), m_writer("records", cam_id, 60) {
  if (!m_reader.open(m_rtsp_url)) {
    throw std::runtime_error("Failed to open RTSP");
  }

  m_writer.init(m_reader.video_stream());

  m_reader.start();
  m_is_running = true;
}

cam::~cam() {
  shutdown();
}

void cam::start_recording() {
  if (m_is_recording) {
    return;
  }

  m_reader.add_sink(&m_writer);
  m_is_recording = true;
}

void cam::stop_recording() {
  if (!m_is_recording) {
    return;
  }

  m_reader.remove_sink(&m_writer);
  m_writer.close();
  m_is_recording = false;
}

void cam::shutdown() {
  if (m_jpeg_streamer) {
    m_reader.remove_sink(m_jpeg_streamer.get());
    m_jpeg_streamer->close();
    m_jpeg_streamer.reset();
  }

  stop_recording();

  if (m_is_running) {
    m_reader.stop();
    m_is_running = false;
  }
}

bool cam::start_jpeg_stream(std::string& error) {
  if (m_jpeg_streamer) {
    error = "jpeg stream already running";
    return false;
  }

  auto streamer = std::make_unique<jpeg_streamer>();
  if (!streamer->init(m_reader.video_stream(), error)) {
    return false;
  }

  m_reader.add_sink(streamer.get());
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

  m_reader.remove_sink(m_jpeg_streamer.get());
  m_jpeg_streamer->close();
  m_jpeg_streamer.reset();

  return true;
}
