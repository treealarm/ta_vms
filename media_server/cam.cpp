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
      m_cam_id(cam_id),
      m_storage_base_path(storage_base_path)
{
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

cam::~cam() 
{
  shutdown();
}

void cam::start_recording() {
  {
    std::scoped_lock lock(m_sinks_mutex);
    if (m_writer) {
      return;
    }

    m_writer =
      std::make_shared<segment_writer>(resolve_storage_base_path(m_storage_base_path), m_cam_id, 60);
  }

  update_reader_subscription();
}

void cam::stop_recording() {
  {
    std::scoped_lock lock(m_sinks_mutex);
    if (!m_writer) {
      return;
    }
    m_writer.reset();
  }

  update_reader_subscription();
}

bool cam::start_web_rtc(std::string& error) {
  {
    std::scoped_lock lock(m_sinks_mutex);

    if (m_webrtc_output_sink) {
      error = "web rtc stream already running";
      return false;
    }

    m_webrtc_output_sink = std::make_shared<webrtc_output_sink>(resolve_default_webrtc_rtsp_url(m_cam_id));
  }

  update_reader_subscription();
  return true;
}

bool cam::stop_web_rtc(std::string& error) {
  {
    std::scoped_lock lock(m_sinks_mutex);

    if (!m_webrtc_output_sink) {
      error = "web rtc stream is not running";
      return false;
    }

    if (m_webrtc_output_sink) {
      m_webrtc_output_sink.reset();
    }
  }

  update_reader_subscription();
  return true;
}

void cam::shutdown()
{
  stop_recording();
  std::string error;
  stop_web_rtc(error);
  {
    std::scoped_lock lock(m_sinks_mutex);
    if (m_jpeg_streamer) {
      m_jpeg_streamer.reset();
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

    auto streamer = std::make_shared<jpeg_streamer>();
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

    m_jpeg_streamer.reset();
  }

  update_reader_subscription();
  return true;
}

void cam::on_packet(const std::shared_ptr<media_packet>& pkt)
{
  std::shared_ptr<segment_writer> writer;
  std::shared_ptr<webrtc_output_sink> webrtc;
  std::shared_ptr<jpeg_streamer> jpeg;

  {
    std::scoped_lock lock(m_sinks_mutex);

    writer = m_writer;
    webrtc = m_webrtc_output_sink;
    jpeg = m_jpeg_streamer;
  }

  // дальше без lock

  if (writer) {
    if (auto p = clone_media_packet(pkt)) {
      writer->on_packet(p);
    }
  }

  if (webrtc &&
    (pkt->media_type == AVMEDIA_TYPE_VIDEO || pkt->media_type == AVMEDIA_TYPE_AUDIO)) {
    if (auto p = clone_media_packet(pkt)) {
      webrtc->on_packet(p);
    }
  }

  if (jpeg) {
    if (auto p = clone_media_packet(pkt)) {
      jpeg->on_packet(p);
    }
  }
}

void cam::update_reader_subscription() 
{
  std::scoped_lock lock(m_sinks_mutex);

  const bool has_consumers = m_writer || m_webrtc_output_sink || m_jpeg_streamer;

  if (has_consumers) 
  {
    m_reader->add_sink(this);
  }
  else
  {
    m_reader->remove_sink(this);
  }
}
