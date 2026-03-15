#include "cam.h"

#include <cstdlib>
#include <stdexcept>

#include "reader/rtsp_reader.h"

namespace {
std::string resolve_webrtc_rtsp_base_url()
{
  if (const char* value = std::getenv("VMS_WEBRTC_RTSP_BASE_URL")) {
    if (value[0] != '\0') {
      return value;
    }
  }

  return "rtsp://localhost:8554";
}

std::string resolve_default_webrtc_rtsp_url(const std::string& camera_id)
{
  std::string base_url = resolve_webrtc_rtsp_base_url();

  if (!base_url.empty() && base_url.back() != '/') {
    base_url.push_back('/');
  }

  return base_url + camera_id;
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
      m_cam_id(cam_id) {
  (void)storage_base_path;

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
  std::string error;
  start_web_rtc(resolve_default_webrtc_rtsp_url(m_cam_id), error);
}

void cam::stop_recording() {
  std::string error;
  stop_web_rtc(error);
}

bool cam::start_web_rtc(const std::string& rtsp_url, std::string& error) {
  {
    std::scoped_lock lock(m_sinks_mutex);

    if (m_is_web_rtc_running) {
      error = "web rtc stream already running";
      return false;
    }

    if (rtsp_url.empty()) {
      error = "rtsp url must not be empty";
      return false;
    }

    m_webrtc_output_sink = std::make_unique<webrtc_output_sink>(rtsp_url);
    m_is_web_rtc_running = true;
    m_is_webrtc_sink_initialized = false;
  }

  update_reader_subscription();
  return true;
}

bool cam::stop_web_rtc(std::string& error) {
  {
    std::scoped_lock lock(m_sinks_mutex);

    if (!m_is_web_rtc_running) {
      error = "web rtc stream is not running";
      return false;
    }

    m_is_web_rtc_running = false;
    m_is_webrtc_sink_initialized = false;

    if (m_webrtc_output_sink) {
      m_webrtc_output_sink->close();
      m_webrtc_output_sink.reset();
    }
  }

  update_reader_subscription();
  return true;
}

void cam::shutdown() {
  {
    std::scoped_lock lock(m_sinks_mutex);
    if (m_jpeg_streamer) {
      m_jpeg_streamer->close();
      m_jpeg_streamer.reset();
    }

    if (m_is_web_rtc_running) {
      m_is_web_rtc_running = false;
      m_is_webrtc_sink_initialized = false;
      if (m_webrtc_output_sink) {
        m_webrtc_output_sink->close();
        m_webrtc_output_sink.reset();
      }
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

  if (m_is_web_rtc_running && m_webrtc_output_sink) {
    if (!m_is_webrtc_sink_initialized) {
      if (pkt->codec_parameters && m_webrtc_output_sink->init(pkt->codec_parameters, pkt->time_base)) {
        m_is_webrtc_sink_initialized = true;
      }
    }

    if (m_is_webrtc_sink_initialized) {
      auto webrtc_packet = clone_media_packet(pkt);
      if (webrtc_packet) {
        m_webrtc_output_sink->on_packet(webrtc_packet);
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

  const bool has_consumers = m_is_web_rtc_running || static_cast<bool>(m_jpeg_streamer);

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
