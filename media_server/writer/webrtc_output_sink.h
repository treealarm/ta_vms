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
  void reset_stream_state();
  bool ensure_initialized(const std::shared_ptr<media_packet>& media);
  bool is_video_key_frame(const std::shared_ptr<media_packet>& media) const;

  rtsp_output_sink m_rtsp_output_sink;
  compat_sink m_video_compat_sink;
  compat_sink m_audio_compat_sink;
  bool m_is_initialized = false;
  avcodec_parameters_ptr m_video_codec_parameters;
  avcodec_parameters_ptr m_audio_codec_parameters;
  AVRational m_video_time_base{};
  AVRational m_audio_time_base{};
  bool m_waiting_for_key_frame = true;
};
