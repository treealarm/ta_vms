#pragma once

#include <atomic>
#include <thread>
#include <vector>
#include <memory>
#include <mutex>
#include <filesystem>
#include <unordered_set>

#include "media/media_packet.h"
#include "media/media_sink.h"
#include "reader.h"

extern "C" {
#include <libavformat/avformat.h>
}

class file_reader final : public reader {
public:
  file_reader();
  file_reader(std::string camera_id, std::string storage_base_path);
  ~file_reader() override;

  bool open(const std::string& source) override;
  void add_sink(media_sink* sink) override;
  void remove_sink(media_sink* sink) override;
  AVStream* video_stream() const override;
  void start() override;
  void stop() override;

private:
  bool connect_current_file();
  bool advance_to_next_file();
  bool initialize_recording_file_sequence(const std::string& start_time);
  void disconnect();
  void read_loop();

  std::vector<std::string> m_file_paths;
  size_t m_current_file_index = 0;
  AVFormatContext* m_ctx = nullptr;
  int m_video_index = -1;
  std::unordered_set<int> m_forward_stream_indexes;
  std::vector<media_sink*> m_sinks;
  mutable std::mutex m_sinks_mutex;

  std::thread m_thread;
  std::atomic<bool> m_running{ false };

  std::string m_camera_id;
  std::filesystem::path m_storage_base_path;
};
