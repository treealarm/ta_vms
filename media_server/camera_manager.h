#pragma once

#include <map>
#include <memory>
#include <mutex>
#include <string>
#include <vector>

#include "arch_player.h"
#include "cam.h"

class camera_manager 
{
public:
  struct camera_info 
  {
    std::string camera_id;
    std::string rtsp_url;
    std::string user;
    std::string pass;
    bool is_recording = false;
    bool is_web_rtc_streaming = false;
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
  bool add_camera(const std::string& camera_id,
                  const std::string& rtsp_url,
    const std::string& user,
    const std::string& pass,
                  bool recording,
                  bool web_rtc,
                  std::string& error);

  bool start_recording(const std::string& camera_id, std::string& error);
  bool stop_recording(const std::string& camera_id, std::string& error);
  bool set_camera_state(
    const std::string& camera_id,
    const std::string& rtsp_url,
    const std::string& user,
    const std::string& pass,
    bool recording,
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
  bool set_player_state(const std::string& client_id,
    const std::string& camera_id,
    const std::chrono::system_clock::time_point& start_time,
    std::optional<std::chrono::milliseconds> position,
    std::string& error);

  bool set_player_command(const std::string& client_id,
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
  std::map<std::string, std::shared_ptr<cam> > m_cameras;
  std::map<std::string, std::shared_ptr<arch_player>> m_arch_players_by_client;
};
