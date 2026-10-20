#pragma once

#include <memory>
#include <mutex>
#include <string>
#include <vector>

#include "interfaces/media_sink.h"
#include "interfaces/reader.h"
#include "recording_types.h"
#include "stream/jpeg_streamer.h"
#include "arch_writer/segment_writer.h"
#include "file_system/utf8_path.h"
#include "writer/analytics_output_sink.h"
#include "writer/webrtc_output_sink.h"
#include "interfaces/web_rtc_utils.h"

class cam_stream final
{
public:
  cam_stream(
    const std::string& rtsp_url,
    const std::string& camera_id,
    const std::string& stream_name,
    const std::string& profile_token,
    const std::string& user,
    const std::string& pass,
    const webrtc_publish_config& webrtc = {},
    bool                         web_rtc_enabled = false);

  ~cam_stream();

  void apply_recording_policy(const recording_policy_spec& policy);
  void stop_recording();

  bool start_web_rtc(std::string& error);
  bool stop_web_rtc(std::string& error);

  /// Publish this stream to a detector at `rtsp_url`, or stop doing so. Idempotent: asking again
  /// with the same address is a no-op, asking with a different one moves the publish, which is
  /// what happens when the camera is reassigned to another worker.
  bool start_analytics_push(const std::string& rtsp_url, uint32_t sample_fps, std::string& error);
  bool stop_analytics_push(std::string& error);
  void update_webrtc_publish(const webrtc_publish_config& cfg, bool web_rtc, std::string& error);

  void shutdown();

  std::string get_source() const { return m_rtsp_url; }
  std::string get_camera_id() const { return m_camera_id; }
  std::string get_stream_name() const { return m_stream_name; }
  std::string get_profile_token() const { return m_profile_token; }
  std::string get_user() const { return m_user; }
  std::string get_pass() const { return m_pass; }
  bool is_recording() const { return m_recording_policy_mode != recording_policy_mode::off; }
  bool is_web_rtc_streaming() const { return m_webrtc_output_sink != nullptr; }
  /// Which encoder the WebRTC path re-compresses this stream with, empty when it is not running or
  /// the camera already publishes H.264 and the packets are copied through.
  std::string video_encoder_name() const;
  bool is_web_rtc_enabled() const { return m_web_rtc_enabled; }
  void set_web_rtc_enabled(bool enabled) { m_web_rtc_enabled = enabled; }
  const std::string& get_webrtc_push_url() const { return m_webrtc.rtsp_push_url; }
  const webrtc_publish_config& get_webrtc_config() const { return m_webrtc; }
  std::string mediamtx_path() const { return m_camera_id + "/" + m_stream_name; }
  recording_policy_mode get_recording_policy_mode() const { return m_recording_policy_mode; }
  std::string current_segment_path() const { return m_writer ? utf8_string(m_writer->current_segment_path()) : std::string(); }

  /// Snapshot of whether the source is currently delivering media. Safe from any thread.
  source_liveness liveness() const { return m_reader ? m_reader->liveness() : source_liveness{}; }
  observed_video observed_video_params() const
  {
    return m_reader ? m_reader->observed_video_params() : observed_video{};
  }

  /// Closes the current segment so it ends on the last real frame instead of spanning the outage.
  /// Called when the source is seen to have dropped: a clip stretched across a gap makes the
  /// archive claim footage it does not have, and hides the gap from detection entirely.
  void close_segment_on_source_loss();

private:
  std::string m_rtsp_url;
  std::string m_camera_id;
  std::string m_stream_name;
  std::string m_profile_token;
  std::string m_user;
  std::string m_pass;
  webrtc_publish_config m_webrtc;
  bool        m_web_rtc_enabled{false};

  std::unique_ptr<reader> m_reader;
  std::shared_ptr<segment_writer> m_writer;
  recording_policy_mode m_recording_policy_mode{recording_policy_mode::off};
  std::chrono::seconds m_record_segment_duration{60};
  std::shared_ptr<webrtc_output_sink> m_webrtc_output_sink;
  std::shared_ptr<analytics_output_sink> m_analytics_output_sink;
  std::string m_analytics_push_url;
  // Kept beside the URL because both identify the sink that is running: the rate decides whether
  // it forwards keyframes only or every packet, and a sink created for one rate cannot serve
  // another. See start_analytics_push.
  uint32_t m_analytics_sample_fps = 0;
  mutable std::mutex m_sinks_mutex;
};
