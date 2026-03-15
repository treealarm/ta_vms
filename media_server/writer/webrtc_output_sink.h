#pragma once

#include <string>

#include "media/media_sink.h"
#include "writer/compat_sink.h"
#include "writer/rtsp_output_sink.h"

class webrtc_output_sink : public media_sink {
public:
  explicit webrtc_output_sink(std::string rtsp_url);

  bool init(const avcodec_parameters_ptr& video_codec_parameters,
            AVRational video_time_base,
            const avcodec_parameters_ptr& audio_codec_parameters,
            AVRational audio_time_base);
  void on_packet(const std::shared_ptr<media_packet>& media) override;
  void close();

private:
  rtsp_output_sink m_rtsp_output_sink;
  compat_sink m_video_compat_sink;
  compat_sink m_audio_compat_sink;
  bool m_is_initialized = false;
};
