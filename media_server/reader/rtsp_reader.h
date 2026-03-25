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
  rtsp_reader();
  ~rtsp_reader() override;

  bool open(const std::string& url) override;
  void add_sink(std::shared_ptr<media_sink> sink) override;
  void remove_sink(std::shared_ptr<media_sink> sink) override;

  void start() override;
  void stop() override;

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
};
