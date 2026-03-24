#pragma once

#include "writer/processors/stream_processor.h"

struct AVCodecContext;
struct AVFrame;
struct SwsContext;

class video_transcode_processor : public stream_processor {
public:
  ~video_transcode_processor() override;

  bool init(const avcodec_parameters_ptr&, AVRational, emit_fn) override;
  void process(const std::shared_ptr<media_packet>&) override;
  void flush() override;
  const avcodec_parameters_ptr& output_codec_parameters() const override;
  AVRational output_time_base() const override;

private:
  void encode_frame(AVFrame* frame);
  void drain_encoder_packets();

private:
  emit_fn m_emit;
  AVCodecContext* m_decoder_ctx = nullptr;
  AVCodecContext* m_encoder_ctx = nullptr;
  SwsContext* m_sws_ctx = nullptr;
  AVFrame* m_scaled_frame = nullptr;
  avcodec_parameters_ptr m_out_codec_parameters;
  AVRational m_out_time_base{};
};
