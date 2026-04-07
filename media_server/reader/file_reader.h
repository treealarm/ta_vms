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
#include "reader/snapshot_reader.h"
#include "sink_container/sink_container_impl.h"
#include "writer/sync_packet_transcoder.h"

extern "C" {
#include <libavformat/avformat.h>
}


class file_reader final : public reader 
{
  using clock = std::chrono::steady_clock;
public:
  explicit file_reader(
    std::string camera_id, 
    std::chrono::milliseconds position,
    std::function<bool(const std::string&)> cb_eof,
    std::function<void(const reader_progress&)> cb_progress
  );
  ~file_reader() override;

  bool open(const std::string& source) override;
  void add_sink(std::shared_ptr<media_sink> sink) override;
  void remove_sink(std::shared_ptr<media_sink> sink) override;
  AVStream* video_stream() const override;
  void start() override;
  void stop() override;
  void pause() override;
  void resume() override;
  bool is_running() const override
  {
    return m_running;
  }
  void step(int32_t steps) override;
  void set_speed(double speed) override;
private:
  bool connect_current_file();
  bool seek_to_position(std::chrono::milliseconds position);
  void disconnect();
  void read_loop();
  void emit_paused_snapshots();
  std::string get_source();
  sync_packet_transcoder m_packet_transcoder;

  std::chrono::milliseconds sync_to_master_clock(int64_t pkt_pts_us);

  avformat_context_ptr m_ctx = nullptr;
  int m_video_index = -1;
  std::unordered_set<int32_t> m_forward_stream_indexes;
  std::thread m_thread;
  std::atomic_bool m_running{ false };

  std::string m_camera_id;
  std::chrono::milliseconds m_start_position_ms;
  std::unique_ptr<snapshot_reader> m_snapshot_reader;
  std::function<bool(const std::string&)> m_on_file_end;
  std::function<void(const reader_progress&)> m_on_progress;

  std::atomic_bool m_paused{ false };
  std::atomic_int32_t m_steps;
  std::atomic<double> m_play_speed = 1.0f;

  sink_container_impl m_sinks_helper;
  std::string m_source;  
  std::mutex m_mutex;

  int64_t m_master_pts = -1; //ms
  clock::time_point m_master_wall_time;
  int32_t m_master_stream_index = -1;

  std::atomic_bool m_drop_until_keyframe = false;
  clock::time_point m_last_pause_emit_at = clock::time_point::min();

  std::chrono::milliseconds m_cur_video_pos;
};
