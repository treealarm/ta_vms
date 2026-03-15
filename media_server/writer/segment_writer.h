#pragma once

#include <chrono>
#include <filesystem>
#include <memory>
#include <string>
#include <unordered_map>

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

  void on_packet(const std::shared_ptr<media_packet>& media) override;
  void close();

private:
  struct input_stream_info {
    avcodec_parameters_ptr codec_parameters;
    AVRational time_base{};
    AVMediaType media_type = AVMEDIA_TYPE_UNKNOWN;
  };

  bool ensure_input_stream(const std::shared_ptr<media_packet>& media);
  void open_new_segment();
  void close_segment();
  std::string make_filename() const;

private:
  std::filesystem::path m_base_dir;
  std::string m_camera_id;
  std::chrono::seconds m_segment_seconds;

  std::unordered_map<int, input_stream_info> m_input_streams;

  AVFormatContext* m_ctx = nullptr;
  std::unordered_map<int, AVStream*> m_output_streams;

  std::chrono::steady_clock::time_point m_segment_start_time;
  std::chrono::system_clock::time_point m_segment_start_wall_time;
  std::filesystem::path m_current_segment_relative_path;

  dapr_publisher m_dapr_publisher;
};
