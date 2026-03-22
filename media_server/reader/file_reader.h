#pragma once

#include <atomic>
#include <thread>
#include <vector>
#include <memory>
#include <mutex>
#include <filesystem>
#include <unordered_set>

#include "interfaces/media_packet.h"
#include "interfaces/media_sink.h"
#include "interfaces/reader.h"
#include "sink_container/sink_container_impl.h"

extern "C" {
#include <libavformat/avformat.h>
}

class file_reader final : public reader {
public:
  file_reader();
  file_reader(std::string camera_id);
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

  AVFormatContext* m_ctx = nullptr;
  int m_video_index = -1;
  std::unordered_set<int> m_forward_stream_indexes;
  std::thread m_thread;
  std::atomic_bool m_running{ false };

  std::string m_camera_id;
  sink_container_impl m_sinks_helper;
  std::string m_source;
  std::mutex m_mutex;
};
