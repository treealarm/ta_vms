#pragma once

#include <chrono>
#include <map>
#include <memory>
#include <mutex>
#include <optional>
#include <string>
#include <vector>

#include "cam_device.h"

class camera_manager
{
public:
  struct stream_config
  {
    std::string stream_name;
    std::string profile_token;
    std::string rtsp_url;
    recording_policy_spec policy;
    bool web_rtc = false;
    std::string webrtc_rtsp_push_url; // override for the MediaMTX RTSP push target
  };

  struct camera_info
  {
    std::string camera_id;
    std::string rtsp_url;
    std::string user;
    std::string pass;
    bool is_recording = false;
    bool is_web_rtc_streaming = false;
    recording_policy_mode recording_policy = recording_policy_mode::off;
  };

  // Real-time runtime state of a single camera stream, for the debug entity-state publisher.
  struct stream_state
  {
    std::string camera_id;
    std::string stream_name;
    bool is_recording = false;
    bool is_web_rtc_streaming = false;
    bool is_web_rtc_enabled = false;
    recording_policy_mode recording_policy = recording_policy_mode::off;
    std::string current_segment; // empty if no segment is currently being written
    // Source liveness, reported to VmsFs so it can derive outage intervals. `last_frame_ts` is the
    // exact boundary of an outage; the report time is deliberately not used for that.
    bool source_alive = false;
    std::optional<std::chrono::system_clock::time_point> last_frame_ts;
    std::optional<std::chrono::system_clock::time_point> recovered_frame_ts;
  };

  /// Closes the open segment of every stream whose source has dropped, so the clip ends on the last
  /// real frame. Idempotent — request_close_segment on an already-closed writer is a no-op.
  void close_segments_on_source_loss();

  // Load all streams from config (called on startup).
  bool configure_camera(
    const std::string& camera_id,
    const std::string& user,
    const std::string& pass,
    const std::vector<stream_config>& streams,
    std::string& error);

  // Add or fully replace a camera with a single unnamed stream (gRPC backward compat).
  bool add_camera(
    const std::string& camera_id,
    const std::string& rtsp_url,
    const std::string& user,
    const std::string& pass,
    const recording_policy_spec& policy,
    bool web_rtc,
    std::string& error);

  bool start_recording(const std::string& camera_id, std::string& error);
  bool stop_recording(const std::string& camera_id, std::string& error);

  // Apply recording policy and (optionally) WebRTC on/off for an existing stream.
  // Config (rtsp_url, credentials, push URL) comes through configure_camera only.
  // web_rtc == nullopt leaves the runtime push state untouched — recording-only state
  // pushes must not stop a push started on demand (live view, analytics).
  bool set_camera_state(
    const std::string& camera_id,
    const std::string& stream_name,
    const recording_policy_spec& policy,
    std::optional<bool> web_rtc,
    std::string& error);

  std::optional<camera_info> get_camera_state(
    const std::string& camera_id,
    std::string& error);

  bool remove_camera(const std::string& camera_id);

  std::vector<camera_info> list_cameras() const;
  // Real-time per-stream state for all configured cameras (for debug entity-state publisher).
  std::vector<stream_state> list_stream_states() const;
  // Starts WebRTC for the given stream if not already streaming. No-op if already running.
  bool ensure_web_rtc(const std::string& camera_id, const std::string& stream_name,
                      std::string& error);

  // Returns shared_ptrs to all webrtc-enabled streams (snapshot under lock).
  std::vector<std::shared_ptr<cam_stream>> webrtc_streams_snapshot() const;
  void stop_all();

private:
  mutable std::mutex m_mutex;
  std::map<std::string, std::shared_ptr<cam_device>> m_cameras;
};
