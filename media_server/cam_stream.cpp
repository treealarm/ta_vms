#include "cam_stream.h"

#include <algorithm>
#include <cstdlib>
#include <stdexcept>

#include "reader/rtsp_reader.h"
#include "interfaces/web_rtc_utils.h"

cam_stream::cam_stream(
  const std::string& rtsp_url,
  const std::string& camera_id,
  const std::string& stream_name,
  const std::string& profile_token,
  const std::string& user,
  const std::string& pass)
  : m_rtsp_url(rtsp_url)
  , m_camera_id(camera_id)
  , m_stream_name(stream_name)
  , m_user(user)
  , m_pass(pass)
{
  m_reader = std::make_unique<rtsp_reader>(camera_id, profile_token, user, pass);

  if (!m_reader->open(m_rtsp_url))
    throw std::runtime_error("Failed to open media source");

  m_reader->start();
}

cam_stream::~cam_stream()
{
  shutdown();
}

void cam_stream::apply_recording_policy(const recording_policy_spec& policy)
{
  if (policy.mode == recording_policy_mode::off)
  {
    stop_recording();
    return;
  }

  const auto seg = std::max(policy.segment_len, std::chrono::seconds(1));

  {
    std::scoped_lock lock(m_sinks_mutex);
    if (m_writer &&
        m_record_segment_duration == seg &&
        m_recording_policy_mode == policy.mode)
    {
      return;
    }
  }

  stop_recording();

  std::shared_ptr<segment_writer> sink;
  {
    std::scoped_lock lock(m_sinks_mutex);
    if (m_writer)
      return;

    m_writer = std::make_shared<segment_writer>(m_camera_id, m_stream_name, seg);
    m_record_segment_duration = seg;
    m_recording_policy_mode = policy.mode;
    sink = m_writer;
  }

  m_reader->add_sink(sink);
}

void cam_stream::stop_recording()
{
  std::shared_ptr<segment_writer> sink;
  {
    std::scoped_lock lock(m_sinks_mutex);
    if (!m_writer)
      return;
    sink = m_writer;
    m_writer.reset();
    m_record_segment_duration = std::chrono::seconds(60);
    m_recording_policy_mode = recording_policy_mode::off;
  }
  if (sink)
    m_reader->remove_sink(sink);
}

bool cam_stream::start_web_rtc(std::string& error)
{
  std::shared_ptr<webrtc_output_sink> sink;
  {
    std::scoped_lock lock(m_sinks_mutex);
    if (m_webrtc_output_sink)
    {
      error = "web rtc stream already running";
      return false;
    }
    m_webrtc_output_sink = std::make_shared<webrtc_output_sink>(
      resolve_default_webrtc_rtsp_url(m_camera_id, m_stream_name));
    sink = m_webrtc_output_sink;
  }
  m_reader->add_sink(sink);
  return true;
}

bool cam_stream::stop_web_rtc(std::string& error)
{
  std::shared_ptr<webrtc_output_sink> sink;
  {
    std::scoped_lock lock(m_sinks_mutex);
    if (!m_webrtc_output_sink)
    {
      error = "web rtc stream is not running";
      return false;
    }
    sink = m_webrtc_output_sink;
    m_webrtc_output_sink.reset();
  }
  if (sink)
    m_reader->remove_sink(sink);
  return true;
}

void cam_stream::shutdown()
{
  stop_recording();
  std::string error;
  stop_web_rtc(error);

  if (m_reader)
    m_reader->stop();
}
