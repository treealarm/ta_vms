#pragma once

#include <string>

#include "writer/processors/stream_processor.h"

struct AVAudioFifo;
struct AVCodecContext;
struct AVFrame;
struct SwrContext;

class audio_transcode_processor : public stream_processor {
public:
  ~audio_transcode_processor() override;

  bool init(const avcodec_parameters_ptr&, AVRational, emit_fn) override;
  void process(const std::shared_ptr<media_packet>&) override;
  void flush() override;
  const avcodec_parameters_ptr& output_codec_parameters() const override;
  AVRational output_time_base() const override;

private:
  void encode_audio_frame(AVFrame* frame);
  void drain_encoder_packets();

private:
  const AVCodecID m_codec_id = AV_CODEC_ID_OPUS;
  
  emit_fn m_emit;
  AVCodecContext* m_decoder_ctx = nullptr;
  AVCodecContext* m_encoder_ctx = nullptr;
  SwrContext* m_swr_ctx = nullptr;
  AVAudioFifo* m_audio_fifo = nullptr;
  avcodec_parameters_ptr m_out_codec_parameters;
  AVRational m_out_time_base{};
};
