#pragma once

#include <string>
#include <vector>

extern "C" {
#include <libavformat/avformat.h>
}

#include "media/media_sink.h"

class rtsp_output_sink : public media_sink {
public:
  struct stream_config {
    AVMediaType media_type = AVMEDIA_TYPE_UNKNOWN;
    avcodec_parameters_ptr codec_parameters;
    AVRational time_base{};
  };

  explicit rtsp_output_sink(std::string rtsp_url);
  ~rtsp_output_sink() override;

  bool init(const std::vector<stream_config>& streams);
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

  stream_state* find_stream(AVMediaType media_type);

private:
  std::string m_rtsp_url;

  AVFormatContext* m_ctx = nullptr;
  std::vector<stream_state> m_streams;
  bool m_is_initialized = false;
};
