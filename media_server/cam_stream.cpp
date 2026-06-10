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
  const std::string& pass,
  const std::string& webrtc_rtsp_push_url,
  bool               web_rtc_enabled)
  : m_rtsp_url(rtsp_url)
  , m_camera_id(camera_id)
  , m_stream_name(stream_name)
  , m_profile_token(profile_token)
  , m_user(user)
  , m_pass(pass)
  , m_webrtc_rtsp_push_url(webrtc_rtsp_push_url)
  , m_web_rtc_enabled(web_rtc_enabled)
{
  m_reader = std::make_unique<rtsp_reader>(camera_id, profile_token, user, pass);

  if (!m_reader->open(m_rtsp_url))
    throw std::runtime_error("Failed to open media source");

  m_reader->start();

  // One segment_writer per stream, for the whole lifetime of cam_stream.
  // Recording on/off is controlled via add_sink/remove_sink in apply_recording_policy().
  m_writer = std::make_shared<segment_writer>(m_camera_id, m_stream_name);
}

cam_stream::~cam_stream()
{
  shutdown();
}

void cam_stream::apply_recording_policy(const recording_policy_spec& policy)
{
  std::scoped_lock lock(m_sinks_mutex);

  if (policy.mode == recording_policy_mode::off)
  {
    if (m_recording_policy_mode == recording_policy_mode::off)
      return;

    m_reader->remove_sink(m_writer);
    m_writer->request_close_segment(std::chrono::seconds(60));
    m_recording_policy_mode = recording_policy_mode::off;
    m_record_segment_duration = std::chrono::seconds(60);
    return;
  }

  const auto seg = std::max(policy.segment_len, std::chrono::seconds(5));
  if (m_recording_policy_mode == policy.mode && m_record_segment_duration == seg)
    return; // already configured as requested

  const bool was_off = (m_recording_policy_mode == recording_policy_mode::off);

  // Just switch the segment duration — keep writing into the current segment
  // (e.g. prealarm -> continuous), no gap waiting for the next keyframe.
  m_writer->set_segment_duration(seg);
  if (was_off)
    m_reader->add_sink(m_writer);

  m_recording_policy_mode = policy.mode;
  m_record_segment_duration = seg;
}

void cam_stream::stop_recording()
{
  apply_recording_policy(recording_policy_spec{ .mode = recording_policy_mode::off });
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
      resolve_webrtc_rtsp_push_url(m_camera_id, m_stream_name, m_webrtc_rtsp_push_url));
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

void cam_stream::update_webrtc_push_url(const std::string& new_url, bool web_rtc, std::string& error)
{
  // Empty URL means MediaMTX not configured — never clobber an already-set push URL.
  // VmsCfg returns "" for cameras without a MediaMTX server assignment.
  if (new_url.empty())
    return;

  const bool was_streaming = is_web_rtc_streaming();
  if (was_streaming)
    stop_web_rtc(error);

  m_webrtc_rtsp_push_url = new_url;
  m_web_rtc_enabled = web_rtc;

  if (web_rtc)
    start_web_rtc(error);
}

void cam_stream::shutdown()
{
  stop_recording();
  std::string error;
  stop_web_rtc(error);

  if (m_reader)
    m_reader->stop();
}
