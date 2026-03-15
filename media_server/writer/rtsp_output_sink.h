#pragma once

#include <string>

extern "C" {
#include <libavformat/avformat.h>
}

#include "media/media_sink.h"

class rtsp_output_sink : public media_sink {
public:
  explicit rtsp_output_sink(std::string rtsp_url);
  ~rtsp_output_sink() override;

  bool init(const avcodec_parameters_ptr& codec_parameters, AVRational time_base);
  void on_packet(const std::shared_ptr<media_packet>& media) override;
  void close();

private:
  std::string m_rtsp_url;

  AVFormatContext* m_ctx = nullptr;
  AVStream* m_out_stream = nullptr;
  int64_t m_last_dts = AV_NOPTS_VALUE;
  int64_t m_last_pts = AV_NOPTS_VALUE;
  bool m_is_initialized = false;
};
