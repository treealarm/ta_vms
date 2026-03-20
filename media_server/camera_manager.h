#pragma once

#include <map>
#include <memory>
#include <mutex>
#include <string>
#include <vector>

#include "arch_player.h"
#include "cam.h"

class camera_manager {
public:
  struct camera_info 
  {
    std::string camera_id;
    std::string rtsp_url;
    bool is_recording = false;
    bool is_web_rtc_streaming = false;
  };
  struct player_info
  {
    std::string camera_id;
    std::string client_id;
    std::chrono::system_clock::time_point start_time;
  };
  bool add_camera(const std::string& camera_id,
                  const std::string& rtsp_url,
                  bool recording,
                  bool web_rtc,
                  std::string& error);

  bool start_recording(const std::string& camera_id, std::string& error);
  bool stop_recording(const std::string& camera_id, std::string& error);
  bool set_camera_state(const std::string& camera_id,
                        bool recording,
                        bool web_rtc,
                        std::string& error);

  bool get_camera_state(
    const std::string& camera_id,
    bool& recording,
    bool& web_rtc,
    std::string& error);

  bool add_arch_player(
                       const std::string& client_id,
                       std::string& error);
  bool remove_camera(const std::string& camera_id, std::string& error);
  bool remove_arch_player(const std::string& client_id, std::string& error);
  bool set_player_state(const std::string& client_id,
                        const std::string& camera_id,
                        const std::chrono::system_clock::time_point& start_time,
                        std::string& error);

  std::vector<camera_info> list_cameras() const;
  std::vector<player_info> list_players() const;
  void stop_all();

private:
  struct camera_entry {
    std::shared_ptr<cam> camera;
    std::string source;
    bool is_recording = false;
    bool is_web_rtc_streaming = false;
  };


  mutable std::mutex m_mutex;
  std::map<std::string, camera_entry> m_cameras;
  std::map<std::string, std::shared_ptr<arch_player>> m_arch_players_by_client;
};
