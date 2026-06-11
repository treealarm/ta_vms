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
#include "writer/webrtc_output_sink.h"

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
    const std::string& webrtc_rtsp_push_url = "",
    bool               web_rtc_enabled      = false);

  ~cam_stream();

  void apply_recording_policy(const recording_policy_spec& policy);
  void stop_recording();

  bool start_web_rtc(std::string& error);
  bool stop_web_rtc(std::string& error);
  void update_webrtc_push_url(const std::string& new_url, bool web_rtc, std::string& error);

  void shutdown();

  std::string get_source() const { return m_rtsp_url; }
  std::string get_camera_id() const { return m_camera_id; }
  std::string get_stream_name() const { return m_stream_name; }
  std::string get_profile_token() const { return m_profile_token; }
  std::string get_user() const { return m_user; }
  std::string get_pass() const { return m_pass; }
  bool is_recording() const { return m_recording_policy_mode != recording_policy_mode::off; }
  bool is_web_rtc_streaming() const { return m_webrtc_output_sink != nullptr; }
  bool is_web_rtc_enabled() const { return m_web_rtc_enabled; }
  void set_web_rtc_enabled(bool enabled) { m_web_rtc_enabled = enabled; }
  const std::string& get_webrtc_push_url() const { return m_webrtc_rtsp_push_url; }
  std::string mediamtx_path() const { return m_camera_id + "/" + m_stream_name; }
  recording_policy_mode get_recording_policy_mode() const { return m_recording_policy_mode; }
  std::string current_segment_path() const { return m_writer ? m_writer->current_segment_path().string() : std::string(); }

private:
  std::string m_rtsp_url;
  std::string m_camera_id;
  std::string m_stream_name;
  std::string m_profile_token;
  std::string m_user;
  std::string m_pass;
  std::string m_webrtc_rtsp_push_url;
  bool        m_web_rtc_enabled{false};

  std::unique_ptr<reader> m_reader;
  std::shared_ptr<segment_writer> m_writer;
  recording_policy_mode m_recording_policy_mode{recording_policy_mode::off};
  std::chrono::seconds m_record_segment_duration{60};
  std::shared_ptr<webrtc_output_sink> m_webrtc_output_sink;
  mutable std::mutex m_sinks_mutex;
};
