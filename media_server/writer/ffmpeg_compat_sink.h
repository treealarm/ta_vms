#pragma once

#include <memory>

extern "C" {
#include <libavcodec/avcodec.h>
#include <libswscale/swscale.h>
}

#include "media/media_sink.h"
#include "writer/segment_writer.h"

class ffmpeg_compat_sink : public media_sink {
public:
  explicit ffmpeg_compat_sink(segment_writer& writer);
  ~ffmpeg_compat_sink() override;

  bool init(const avcodec_parameters_ptr& codec_parameters, AVRational time_base);
  void on_packet(const std::shared_ptr<media_packet>& media) override;
  void close();

private:
  enum class mode {
    unknown,
    copy_h264,
    encode_libx264,
    failed
  };

  bool init_copy_mode(const avcodec_parameters_ptr& codec_parameters, AVRational time_base);
  bool init_transcode_mode(const avcodec_parameters_ptr& codec_parameters, AVRational time_base);
  void transcode_packet(const std::shared_ptr<media_packet>& media);
  void encode_frame(AVFrame* frame);
  void drain_encoder_packets();
  void flush_decoder_encoder();

private:
  segment_writer& m_writer;
  mode m_mode = mode::unknown;
  bool m_is_writer_initialized = false;

  AVCodecContext* m_decoder_ctx = nullptr;
  AVCodecContext* m_encoder_ctx = nullptr;
  SwsContext* m_sws_ctx = nullptr;

  avcodec_parameters_ptr m_out_codec_parameters;
  AVRational m_out_time_base{};

  AVFrame* m_scaled_frame = nullptr;
};
