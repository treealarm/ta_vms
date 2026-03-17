#include "writer/video_copy_processor.h"

#include "media/media_packet.h"

bool video_copy_processor::init(const avcodec_parameters_ptr& codec_parameters, AVRational time_base, emit_fn emit)
{
  m_emit = std::move(emit);
  m_out_codec_parameters = codec_parameters;
  m_out_time_base = time_base;
  return static_cast<bool>(m_out_codec_parameters);
}

void video_copy_processor::process(const std::shared_ptr<media_packet>& media)
{
  if (!m_emit || !media) {
    return;
  }

  auto out_media = clone_media_packet(media);
  if (!out_media) {
    return;
  }

  out_media->codec_parameters = m_out_codec_parameters;
  out_media->time_base = m_out_time_base;
  m_emit(out_media);
}

void video_copy_processor::flush() {}

const avcodec_parameters_ptr& video_copy_processor::output_codec_parameters() const
{
  return m_out_codec_parameters;
}

AVRational video_copy_processor::output_time_base() const
{
  return m_out_time_base;
}
