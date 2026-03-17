#pragma once

#include "writer/stream_processor.h"

class audio_copy_processor : public stream_processor {
public:
  bool init(const avcodec_parameters_ptr&, AVRational, emit_fn) override;
  void process(const std::shared_ptr<media_packet>&) override;
  void flush() override;
  const avcodec_parameters_ptr& output_codec_parameters() const override;
  AVRational output_time_base() const override;

private:
  emit_fn m_emit;
  avcodec_parameters_ptr m_out_codec_parameters;
  AVRational m_out_time_base{};
};
