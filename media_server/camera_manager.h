#pragma once

#include <map>
#include <memory>
#include <mutex>
#include <string>
#include <vector>

#include "arch_player.h"
#include "cam_device.h"

class camera_manager
{
public:
  struct stream_config
  {
    std::string stream_name;
    std::string rtsp_url;
    recording_policy_spec policy;
    bool web_rtc = false;
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

  struct player_info
  {
    std::string camera_id;
    std::string client_id;
    std::chrono::system_clock::time_point position_ts;
    std::optional<std::filesystem::path> file_path;
    std::optional<std::chrono::system_clock::time_point> start_ts;
    std::optional<reader_progress> progress;
    std::optional<player_mode> mode;
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
    const std::string& rtsp_url,
    const std::string& user,
    const std::string& pass,
    const recording_policy_spec& policy,
    bool web_rtc,
    std::string& error);

  std::optional<camera_info> get_camera_state(
    const std::string& camera_id,
    std::string& error);

  bool add_arch_player(
    const std::string& client_id,
    const std::string& camera_id,
    std::string& error);

  bool remove_camera(const std::string& camera_id, std::string& error);
  bool remove_arch_player(const std::string& client_id, std::string& error);

  bool set_player_state(
    const std::string& client_id,
    const std::string& camera_id,
    const std::optional<std::chrono::system_clock::time_point> start_time,
    std::optional<std::chrono::milliseconds> position,
    std::string& error);

  bool set_player_command(
    const std::string& client_id,
    const player_command& cmd,
    std::string& error);

  std::optional<player_info> get_player_state(
    const std::string& client_id,
    std::string& error);

  std::vector<camera_info> list_cameras() const;
  std::vector<player_info> list_players() const;
  void stop_all();

private:
  mutable std::mutex m_mutex;
  std::map<std::string, std::shared_ptr<cam_device>> m_cameras;
  std::map<std::string, std::shared_ptr<arch_player>> m_arch_players_by_client;
};
