#pragma once

#include <chrono>
#include <filesystem>
#include <memory>
#include <string>
#include <unordered_map>

extern "C" {
#include <libavformat/avformat.h>
}

#include "grpc_layer/dapr_publisher.h"
#include "interfaces/media_packet.h"
#include "interfaces/media_sink.h"

class segment_writer : public media_sink
{
public:
  segment_writer(
                 const std::string& camera_id,
                 int segment_seconds);
  ~segment_writer() override;
  void on_packet(const std::shared_ptr<media_packet>& media) override;
private:
  struct input_stream_info 
  {
    avcodec_parameters_ptr codec_parameters;
    AVRational time_base{};
    AVMediaType media_type = AVMEDIA_TYPE_UNKNOWN;
  };

  bool ensure_input_stream(const std::shared_ptr<media_packet>& media);
  void open_new_segment();
  void close_segment();
  void close();
private:
  std::string m_camera_id;
  std::chrono::seconds m_segment_seconds;

  std::unordered_map<int, input_stream_info> m_input_streams;

  
  avformat_context_ptr m_ctx = nullptr;

  std::unordered_map<int, AVStream*> m_output_streams;

  std::chrono::steady_clock::time_point m_segment_start_time;

  dapr_publisher m_dapr_publisher;

  struct stream_timing_state {
    int64_t first_pts = AV_NOPTS_VALUE;
    int64_t first_dts = AV_NOPTS_VALUE;
    int64_t last_pts = AV_NOPTS_VALUE;
    int64_t last_dts = AV_NOPTS_VALUE;
  };

  std::unordered_map<int, stream_timing_state> m_timing;
};
