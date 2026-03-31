#pragma once

#include <atomic>
#include <chrono>
#include <functional>
#include <memory>
#include <mutex>
#include <string>
#include <thread>

#include "interfaces/media_packet.h"
#include "interfaces/media_sink.h"
#include "interfaces/reader.h"
#include "sink_container/sink_container_impl.h"

extern "C" {
#include <libavcodec/avcodec.h>
#include <libavformat/avformat.h>
#include <libavutil/opt.h>
}

class snapshot_reader
{
public:
  explicit snapshot_reader(
    std::chrono::milliseconds position);
  ~snapshot_reader();

  bool open(const std::string& source);

  AVStream* video_stream();
  void start();
  void stop();

  bool is_running() const
  {
    return m_running;
  }

private:
  bool connect_current_file(const std::string& source);
  bool decode_to_snapshot(std::chrono::milliseconds position);
  void read_loop();
  void disconnect();
  std::string get_source();
  bool init_encoder(AVStream* stream);

  avformat_context_ptr m_ctx = nullptr;
  int m_video_index = -1;
  std::chrono::milliseconds m_position;

  avframe_ptr m_snapshot_frame;

  AVCodecContext* m_encoder_ctx = nullptr;
  AVRational m_encoder_time_base{ 1, 25 };

  std::thread m_thread;
  std::atomic_bool m_running{ false };
  std::atomic_bool m_paused{ false };

  std::string m_source;
  mutable std::mutex m_mutex;
};
