#pragma once

#include <atomic>
#include <thread>
#include <vector>
#include <memory>
#include <mutex>

#include "media/media_packet.h"
#include "media/media_sink.h"
#include "reader.h"

extern "C" {
#include <libavformat/avformat.h>
}

class file_reader final : public reader {
public:
  file_reader();
  ~file_reader() override;

  bool open(const std::string& source) override;
  void add_sink(media_sink* sink) override;
  void remove_sink(media_sink* sink) override;
  AVStream* video_stream() const override;
  void start() override;
  void stop() override;

private:
  bool connect();
  void disconnect();
  void read_loop();

  std::string m_file_path;
  AVFormatContext* m_ctx = nullptr;
  int m_video_index = -1;
  std::vector<media_sink*> m_sinks;
  mutable std::mutex m_sinks_mutex;

  std::thread m_thread;
  std::atomic<bool> m_running{ false };
};
