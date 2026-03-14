#pragma once

#include <atomic>
#include <memory>
#include <mutex>
#include <thread>

#include "media/media_packet.h"
#include "media/media_sink.h"
#include "reader.h"

extern "C" {
#include <libavformat/avformat.h>
#include <libavutil/time.h>
}

class rtsp_reader final : public reader {
public:
  rtsp_reader();
  ~rtsp_reader() override;

  bool open(const std::string& url) override;
  void add_sink(media_sink* sink) override;
  void remove_sink(media_sink* sink) override;

  void start() override;
  void stop() override;

  AVStream* video_stream() const override;

private:
  bool connect();
  void disconnect();
  void read_loop();

  std::string m_rtsp_url;
  avformat_context_ptr m_ctx = nullptr;
  int64_t m_interrupt_start = 0;
  int m_video_index = -1;

  media_sink* m_sink = nullptr;
  mutable std::mutex m_sink_mutex;

  std::thread m_thread;
  std::atomic<bool> m_running{ false };
};
