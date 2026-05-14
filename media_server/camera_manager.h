#pragma once

#include <map>
#include <memory>
#include <mutex>
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

  bool set_camera_state(
    const std::string& camera_id,
    const std::string& stream_name,
    const std::string& rtsp_url,
    const std::string& user,
    const std::string& pass,
    const recording_policy_spec& policy,
    bool web_rtc,
    std::string& error,
    const std::string& webrtc_rtsp_push_url = "");

  std::optional<camera_info> get_camera_state(
    const std::string& camera_id,
    std::string& error);

  bool remove_camera(const std::string& camera_id, std::string& error);

  std::vector<camera_info> list_cameras() const;
  void stop_all();

private:
  mutable std::mutex m_mutex;
  std::map<std::string, std::shared_ptr<cam_device>> m_cameras;
};
