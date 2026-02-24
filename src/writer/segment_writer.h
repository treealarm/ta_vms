#pragma once

#include <chrono>
#include <filesystem>
#include <memory>
#include <string>

extern "C" {
#include <libavformat/avformat.h>
}

#include "media/media_packet.h"
#include "media/media_sink.h"

class segment_writer : public media_sink {
public:
  segment_writer(const std::string& base_dir,
    const std::string& camera_id,
    int segment_seconds);

  bool init(AVStream* in_stream);

  void on_packet(const std::shared_ptr<media_packet>& media) override;
  void close();

private:
  void open_new_segment();
  void close_segment();
  std::string make_filename() const;

private:
  std::filesystem::path m_base_dir;
  std::string m_camera_id;
  int m_segment_seconds = 10;

  AVStream* m_in_stream = nullptr;
  AVStream* m_out_stream = nullptr;
  AVFormatContext* m_ctx = nullptr;

  std::chrono::steady_clock::time_point m_segment_start_time{};
  int64_t m_next_dts = 0;
  avpacket_ptr m_last_keyframe; // последний ключевой кадр для следующего сегмента
  int64_t m_last_written_pts = AV_NOPTS_VALUE; // PTS последнего кадра
};