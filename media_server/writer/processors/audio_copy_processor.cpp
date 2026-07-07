#include "writer/processors/audio_copy_processor.h"

#include "interfaces/media_packet.h"

bool audio_copy_processor::init(const avcodec_parameters_ptr& codec_parameters, AVRational time_base, AVRational, emit_fn emit)
{
  m_emit = std::move(emit);
  m_out_codec_parameters = codec_parameters;
  m_out_time_base = time_base;
  return static_cast<bool>(m_out_codec_parameters);
}

void audio_copy_processor::process(const std::shared_ptr<media_packet>& media)
{
  if (!m_emit || !media) {
    return;
  }

  m_emit(media);
}

void audio_copy_processor::flush() {}

const avcodec_parameters_ptr& audio_copy_processor::output_codec_parameters() const
{
  return m_out_codec_parameters;
}

AVRational audio_copy_processor::output_time_base() const
{
  return m_out_time_base;
}
