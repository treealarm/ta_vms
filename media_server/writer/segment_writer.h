#pragma once

#include <string>
#include <memory>
#include <filesystem>
#include <chrono>

extern "C" {
#include <libavformat/avformat.h>
}

#include "dapr_publisher.h"
#include "media/media_packet.h"
#include "media/media_sink.h"

class segment_writer : public media_sink {
public:
  segment_writer(const std::string& base_dir,
    const std::string& camera_id,
    int segment_seconds);

  bool init(const avcodec_parameters_ptr& codec_parameters, AVRational time_base);
  void on_packet(const std::shared_ptr<media_packet>& media) override;
  void close();

private:
  void open_new_segment();
  void close_segment();
  std::string make_filename() const;

private:
  std::filesystem::path m_base_dir;
  std::string m_camera_id;
  std::chrono::seconds m_segment_seconds;

  avcodec_parameters_ptr m_in_codec_parameters;
  AVRational m_in_time_base{};

  AVFormatContext* m_ctx = nullptr;
  AVStream* m_out_stream = nullptr;

  std::chrono::steady_clock::time_point m_segment_start_time;
  std::chrono::system_clock::time_point m_segment_start_wall_time;
  std::filesystem::path m_current_segment_relative_path;

  int64_t m_pts = 0;
  dapr_publisher m_dapr_publisher;
};
