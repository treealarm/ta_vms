#pragma once

#include <atomic>
#include <chrono>
#include <memory>
#include <string>
#include <thread>
#include <unordered_map>

#include "camera_manager.h"
#include "grpc_layer/utils_client.h"

class mediamtx_monitor
{
public:
  mediamtx_monitor(camera_manager& cameras, utils_client& utils,
                   std::chrono::seconds idle_timeout = std::chrono::seconds(30));
  ~mediamtx_monitor();

  void start();
  void stop();

private:
  void poll_loop();

  camera_manager& m_cameras;
  utils_client&   m_utils;
  std::chrono::seconds m_idle_timeout;

  // key: "camera_id/stream_name" → time of last observed reader activity
  std::unordered_map<std::string, std::chrono::steady_clock::time_point> m_last_active;

  std::thread       m_thread;
  std::atomic<bool> m_stop{false};

  static constexpr std::chrono::seconds k_poll_interval{5};
};
