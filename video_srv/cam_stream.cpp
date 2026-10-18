#include "cam_stream.h"

#include <algorithm>
#include <cstdlib>
#include <stdexcept>

#include "grpc_layer/onvif_metadata_client.h"
#include "reader/file_camera_reader.h"
#include "reader/rtsp_reader.h"
#include "interfaces/web_rtc_utils.h"

cam_stream::cam_stream(
  const std::string& rtsp_url,
  const std::string& camera_id,
  const std::string& stream_name,
  const std::string& profile_token,
  const std::string& user,
  const std::string& pass,
  const webrtc_publish_config& webrtc,
  bool                         web_rtc_enabled)
  : m_rtsp_url(rtsp_url)
  , m_camera_id(camera_id)
  , m_stream_name(stream_name)
  , m_profile_token(profile_token)
  , m_user(user)
  , m_pass(pass)
  , m_webrtc(webrtc)
  , m_web_rtc_enabled(web_rtc_enabled)
{
  if (profile_token.empty() && file_camera_reader::is_file_source(rtsp_url))
  {
    m_reader = std::make_unique<file_camera_reader>(camera_id, stream_name);
  }
  else
  {
    auto rtsp = std::make_unique<rtsp_reader>(camera_id, profile_token, user, pass);
    // Only a live camera can carry a metadata track, so the handler is attached on the concrete
    // reader rather than widened into the reader interface. It fires on the reader thread, which
    // is also the only thread allowed to touch video_stream() — hence reading the resolution here
    // instead of caching it.
    rtsp->set_metadata_handler(
      [this, raw = rtsp.get()](std::string&& document, std::chrono::system_clock::time_point stamp)
      {
        int width = 0;
        int height = 0;
        if (const AVStream* video = raw->video_stream())
        {
          width = video->codecpar->width;
          height = video->codecpar->height;
        }
        onvif_metadata_client::instance().enqueue(
          m_camera_id, m_stream_name, std::move(document), width, height, stamp);
      });
    m_reader = std::move(rtsp);
  }

  if (!m_reader->open(m_rtsp_url))
    throw std::runtime_error("Failed to open media source");

  // One segment_writer per stream, for the whole lifetime of cam_stream.
  // Recording on/off is controlled via add_sink/remove_sink in apply_recording_policy().
  // Built before the reader runs: the source-lost handler below reaches for it from the reader
  // thread, which must not race with this assignment.
  m_writer = std::make_shared<segment_writer>(m_camera_id, m_stream_name);

  // The reader knows it lost the source seconds before the liveness sweep asks. Closing the clip
  // at the transition is what keeps the footage that arrives after a reconnect out of a segment
  // opened for the previous session — see close_segment_on_source_loss.
  m_reader->set_source_lost_handler([this] { close_segment_on_source_loss(); });

  m_reader->start();
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

std::string cam_stream::video_encoder_name() const
{
  std::shared_ptr<webrtc_output_sink> sink;
  {
    std::scoped_lock lock(m_sinks_mutex);
    sink = m_webrtc_output_sink;
  }
  return sink ? sink->video_encoder_name() : std::string{};
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
      resolve_webrtc_rtsp_push_url(m_camera_id, m_stream_name, m_webrtc.rtsp_push_url),
      m_webrtc.transcode_hevc);
    sink = m_webrtc_output_sink;
  }
  m_reader->add_sink(sink);
  return true;
}

bool cam_stream::start_analytics_push(
  const std::string& rtsp_url, uint32_t sample_fps, std::string& error)
{
  if (rtsp_url.empty())
  {
    error = "analytics push url is empty";
    return false;
  }

  std::shared_ptr<analytics_output_sink> previous;
  std::shared_ptr<analytics_output_sink> started;
  {
    std::scoped_lock lock(m_sinks_mutex);
    if (m_analytics_output_sink
      && m_analytics_push_url == rtsp_url
      && m_analytics_sample_fps == sample_fps)
      return true;  // already publishing there, at the rate asked for

    // A different address means the camera moved to another worker; a different rate means the
    // sink would forward the wrong thing. Comparing only the address left a sink running with the
    // rate it was born with, and since that rate decides keyframes-only against every packet, a
    // watch raised from 1 to 4 fps went on receiving one frame a second: the clip keys once a
    // second, so keyframes-only had been the right answer at 1 fps and was silently kept.
    //
    // Swap rather than refuse: the caller owns the assignment and has already decided. The brief
    // reconnect is the same one a worker change costs, and a rate change is just as rare.
    previous = m_analytics_output_sink;
    m_analytics_output_sink = std::make_shared<analytics_output_sink>(rtsp_url, sample_fps);
    m_analytics_push_url = rtsp_url;
    m_analytics_sample_fps = sample_fps;
    started = m_analytics_output_sink;
  }

  if (previous)
    m_reader->remove_sink(previous);
  m_reader->add_sink(started);
  return true;
}

bool cam_stream::stop_analytics_push(std::string& error)
{
  std::shared_ptr<analytics_output_sink> sink;
  {
    std::scoped_lock lock(m_sinks_mutex);
    if (!m_analytics_output_sink)
    {
      error = "analytics push is not running";
      return false;
    }
    sink = m_analytics_output_sink;
    m_analytics_output_sink.reset();
    m_analytics_push_url.clear();
    m_analytics_sample_fps = 0;
  }
  m_reader->remove_sink(sink);
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

void cam_stream::update_webrtc_publish(const webrtc_publish_config& cfg, bool web_rtc, std::string& error)
{
  // Empty URL means MediaMTX not configured — never clobber an already-set push URL. VmsCfg
  // returns "" for cameras without a MediaMTX server assignment, and a deployment that points at
  // its router through VMS_WEBRTC_RTSP_BASE_URL has it empty always. Only the URL is protected:
  // returning here outright also discarded transcode_hevc, so on such a deployment the router's
  // switch changed the database and nothing else.
  webrtc_publish_config next = cfg;
  if (next.rtsp_push_url.empty())
    next.rtsp_push_url = m_webrtc.rtsp_push_url;

  if (next.rtsp_push_url == m_webrtc.rtsp_push_url && next.transcode_hevc == m_webrtc.transcode_hevc)
    return;

  const bool was_streaming = is_web_rtc_streaming();
  if (was_streaming)
    stop_web_rtc(error);

  m_webrtc = next;
  m_web_rtc_enabled = web_rtc;

  if (web_rtc)
    start_web_rtc(error);
}

void cam_stream::close_segment_on_source_loss()
{
  if (!m_writer || m_recording_policy_mode == recording_policy_mode::off)
    return;
  // Non-blocking and processed in order with the packet queue — which is empty during an outage,
  // so the close happens right away. Called from two places: the reader, at the instant it declares
  // the source lost, and the periodic liveness sweep, which stays as a backstop for sources that
  // never report a transition. Idempotent, so both firing costs nothing.
  m_writer->request_close_segment(m_record_segment_duration);
}

void cam_stream::shutdown()
{
  stop_recording();
  std::string error;
  stop_web_rtc(error);

  if (m_reader)
    m_reader->stop();
}
