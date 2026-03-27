#pragma once

#include <atomic>
#include <thread>
#include <vector>
#include <memory>
#include <mutex>
#include <filesystem>
#include <unordered_set>
#include <functional>
#include <chrono>

#include "interfaces/media_packet.h"
#include "interfaces/media_sink.h"
#include "interfaces/reader.h"
#include "sink_container/sink_container_impl.h"

extern "C" {
#include <libavformat/avformat.h>

}

class file_reader final : public reader 
{
  using clock = std::chrono::steady_clock;
public:
  explicit file_reader(std::string camera_id, std::function<bool(const std::string&)> cb_eof);
  ~file_reader() override;

  bool open(const std::string& source) override;
  void add_sink(std::shared_ptr<media_sink> sink) override;
  void remove_sink(std::shared_ptr<media_sink> sink) override;
  AVStream* video_stream() const override;
  void start() override;
  void stop() override;

private:
  bool connect_current_file(const std::string& source);
  void disconnect();
  void read_loop();
  std::string get_source();
  // ¬озвращает true, если нужно ждать перед отправкой пакета, false если можно сразу
  std::chrono::microseconds sync_to_master_clock(int64_t pkt_pts_us);

  avformat_context_ptr m_ctx = nullptr;
  int m_video_index = -1;
  std::unordered_set<int32_t> m_forward_stream_indexes;
  std::thread m_thread;
  std::atomic_bool m_running{ false };

  std::string m_camera_id;
  std::function<bool(const std::string&)> m_on_file_end;

  sink_container_impl m_sinks_helper;
  std::string m_source;
  std::mutex m_mutex;

  int64_t m_master_pts_us = -1;
  clock::time_point m_master_wall_time;
  int32_t m_master_stream_index = -1;
};
