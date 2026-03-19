#include "cam.h"

#include <cstdlib>
#include <stdexcept>

#include "reader/rtsp_reader.h"
#include "interfaces/storage_utils.h"

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
}

cam::~cam() 
{
  shutdown();
}

void cam::start_recording() 
{
  std::shared_ptr<segment_writer> sink;
  {
    std::scoped_lock lock(m_sinks_mutex);
    if (m_writer) 
    {
      return;
    }

    m_writer =
      std::make_shared<segment_writer>(resolve_storage_base_path(m_storage_base_path), m_cam_id, 60);
    sink = m_writer;
  }

  m_reader->add_sink(sink);
}

void cam::stop_recording() 
{
  std::shared_ptr<segment_writer> sink;
  {
    std::scoped_lock lock(m_sinks_mutex);
    if (!m_writer) 
    {
      return;
    }
    sink = m_writer;
    m_writer.reset();
  }
  if (sink)
  {
    m_reader->remove_sink(sink);
  }
}

bool cam::start_web_rtc(std::string& error) 
{
  std::shared_ptr<webrtc_output_sink>  sink;
  {
    std::scoped_lock lock(m_sinks_mutex);

    if (m_webrtc_output_sink) 
    {
      error = "web rtc stream already running";
      return false;
    }

    m_webrtc_output_sink = std::make_shared<webrtc_output_sink>(resolve_default_webrtc_rtsp_url(m_cam_id));
    sink = m_webrtc_output_sink;
  }
  m_reader->add_sink(sink);
  return true;
}

bool cam::stop_web_rtc(std::string& error) 
{
  std::shared_ptr<webrtc_output_sink>  sink;
  {
    std::scoped_lock lock(m_sinks_mutex);

    if (!m_webrtc_output_sink) 
    {
      error = "web rtc stream is not running";
      return false;
    }

    if (m_webrtc_output_sink) 
    {
      sink = m_webrtc_output_sink;
      m_webrtc_output_sink.reset();
    }
  }
  if (sink) 
  {
    m_reader->remove_sink(sink);
  }
  return true;
}

void cam::shutdown()
{
  stop_recording();
  std::string error;
  stop_web_rtc(error);

  if (m_reader) {
    m_reader->stop();
  }
}


