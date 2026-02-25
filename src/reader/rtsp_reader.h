#pragma once
#include <atomic>
#include <thread>
#include <vector>
#include <memory>
#include <mutex>
#include "media/media_packet.h"
#include "media/media_sink.h"

extern "C" {
#include <libavformat/avformat.h>
}

class rtsp_reader {
public:
  rtsp_reader();
  ~rtsp_reader();

  bool open(const std::string& url);
  void add_sink(media_sink* sink);
  void remove_sink(media_sink* sink);

  void start();  // запуск потока
  void stop();   // корректная остановка

  AVStream* video_stream() const;

private:
  void read_loop();

  std::string m_rtsp_url;
  AVFormatContext* m_ctx = nullptr;
  int m_video_index = -1;
  std::vector<media_sink*> m_sinks;
  mutable std::mutex m_sinks_mutex;

  std::thread m_thread;
  std::atomic<bool> m_running{ false };
};