#pragma once

#include <string>
#include <vector>

extern "C" {
#include <libavformat/avformat.h>
}

#include "interfaces/media_sink.h"

class rtsp_output_sink : public media_sink {
public:
  struct stream_config {
    AVMediaType media_type = AVMEDIA_TYPE_UNKNOWN;
    avcodec_parameters_ptr codec_parameters;
    AVRational time_base{};
  };

  explicit rtsp_output_sink(std::string rtsp_url);
  ~rtsp_output_sink() override;

  void on_packet(const std::shared_ptr<media_packet>& media) override;
  void close();

private:
  struct stream_state {
    AVMediaType media_type = AVMEDIA_TYPE_UNKNOWN;
    AVStream* out_stream = nullptr;
    AVRational input_time_base{};
    int64_t last_dts = AV_NOPTS_VALUE;
    int64_t last_pts = AV_NOPTS_VALUE;
  };
  bool init(const std::vector<stream_config>& streams);
  stream_state* find_stream(AVMediaType media_type);
  bool update_stream_config_from_packet(const std::shared_ptr<media_packet>& media);
  std::vector<stream_config> collect_known_streams() const;

private:
  std::string m_rtsp_url;

  avformat_context_ptr m_ctx = nullptr;
  std::vector<stream_state> m_streams;
  bool m_is_initialized = false;
  bool m_wait_keyframe = true;
  int m_consecutive_write_errors = 0;
  stream_config m_video_config;
  stream_config m_audio_config;

  int64_t m_last_master_pts = AV_NOPTS_VALUE;
  AVRational m_master_time_base{ 1, 90000 }; // видео как master
};
