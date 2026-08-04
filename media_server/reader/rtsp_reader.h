#pragma once

#include <atomic>
#include <unordered_set>
#include <memory>
#include <mutex>
#include <thread>

#include "interfaces/media_packet.h"
#include "interfaces/media_sink.h"
#include "interfaces/reader.h"

#include "sink_container/sink_container_impl.h"

extern "C" {
#include <libavformat/avformat.h>
#include <libavutil/time.h>
}

class rtsp_reader final : public reader {
public:
  rtsp_reader(const std::string& cam_id, const std::string& profile_token, const std::string& user, const std::string& pass);
  ~rtsp_reader() override;

  bool open(const std::string& url) override;
  void add_sink(std::shared_ptr<media_sink> sink) override;
  void remove_sink(std::shared_ptr<media_sink> sink) override;

  void start() override;
  void stop() override;

  void pause() override
  {
  };
  void resume() override
  {
  };
  bool is_running() const override
  {
    return m_running;
  }
  source_liveness liveness() const override;
  void step(int32_t /*steps*/) override {};
  void set_speed(double /*speed*/) override {};
  AVStream* video_stream() const override;

private:
  bool connect();
  void disconnect();
  void read_loop();

  std::string m_rtsp_url;
  avformat_context_ptr m_ctx = nullptr;
  int m_video_index = -1;
  std::unordered_set<int> m_forward_stream_indexes;

  sink_container_impl m_sinks_helper;

  std::thread m_thread;
  std::atomic_bool m_running{ false };

  // Read by the reporting timer from another thread, written only by the read loop — hence atomic.
  // Kept here rather than derived from m_ctx because those members are plain and reader-thread-only.
  std::atomic_bool m_source_alive{ false };
  // Microseconds since the epoch; 0 = no packet has ever arrived. A time_point is not trivially
  // copyable enough for std::atomic on every toolchain, so the count travels instead.
  std::atomic_int64_t m_last_frame_us{ 0 };

  std::string m_cam_id;
  std::string m_profile_token;
  std::string m_user;
  std::string m_password;
};
