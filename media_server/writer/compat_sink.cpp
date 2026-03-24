#include "writer/compat_sink.h"

#include <algorithm>

#include "interfaces/media_packet.h"
#include "writer/audio_copy_processor.h"
#include "writer/audio_transcode_processor.h"
#include "writer/video_copy_processor.h"
#include "writer/video_transcode_processor.h"


#include "av_utils/need_reinit.h"

compat_sink::~compat_sink()
{
  close();
}

bool compat_sink::init(const avcodec_parameters_ptr& codec_parameters, AVRational time_base)
{
  close();

  std::lock_guard<std::mutex> lock(m_mutex);

  m_processor = create_processor(codec_parameters);
  if (!m_processor) 
  {
    return false;
  }

  if (!m_processor->init(codec_parameters, time_base, [this](const std::shared_ptr<media_packet>& media)
    { emit_packet(media); })) 
  {
    m_processor.reset();
    return false;
  }

  m_in_codec_parameters = codec_parameters;
  m_in_time_base = time_base;
  m_out_codec_parameters = m_processor->output_codec_parameters();
  m_out_time_base = m_processor->output_time_base();
  return true;
}

std::unique_ptr<stream_processor> compat_sink::create_processor(const avcodec_parameters_ptr& codec_parameters) const
{
  if (!codec_parameters) 
  {
    return nullptr;
  }

  if (codec_parameters->codec_type == AVMEDIA_TYPE_VIDEO)
  {
    if (codec_parameters->codec_id == AV_CODEC_ID_H264) 
    {
      return std::make_unique<video_copy_processor>();
    }

    return std::make_unique<video_transcode_processor>();
  }

  if (codec_parameters->codec_type == AVMEDIA_TYPE_AUDIO) 
  {
    if (codec_parameters->codec_id == AV_CODEC_ID_OPUS) 
    {
      return std::make_unique<audio_copy_processor>();
    }

    return std::make_unique<audio_transcode_processor>();
  }

  return nullptr;
}

void compat_sink::add_sink(std::shared_ptr<media_sink> sink)
{
  m_sinks_helper.add_sink(sink);
}

void compat_sink::remove_sink(std::shared_ptr<media_sink> sink)
{
  m_sinks_helper.remove_sink(sink);
}

bool compat_sink::should_reinitialize(const std::shared_ptr<media_packet>& media) const
{
  std::lock_guard<std::mutex> lock(m_mutex);

  if (!media || !media->codec_parameters)
    return false;

  if (!m_processor || !m_in_codec_parameters)
    return true;

  return av_utils::need_reinit(
    m_in_codec_parameters.get(),
    m_in_time_base,
    media->codec_parameters.get(),
    media->time_base
  );
}

void compat_sink::on_packet(const std::shared_ptr<media_packet>& media)
{
  if (!media || !media->packet || !media->codec_parameters) 
  {
    return;
  }

  if (should_reinitialize(media)) 
  {
    if (!init(media->codec_parameters, media->time_base)) 
    {
      return;
    }

  }

  std::lock_guard<std::mutex> lock(m_mutex);

  if (!m_processor)
  {
    return;
  }

  m_processor->process(media);
}

void compat_sink::emit_packet(const std::shared_ptr<media_packet>& media)
{
  auto sinks_copy = m_sinks_helper.snapshot_sinks();

  for (auto sink : sinks_copy) {
    if (!sink) {
      continue;
    }

    auto sink_media = clone_media_packet(media);
    if (!sink_media) {
      continue;
    }

    sink->on_packet(sink_media);
  }
}

void compat_sink::close()
{
  std::lock_guard<std::mutex> lock(m_mutex);

  if (m_processor) 
  {
    m_processor->flush();
    m_processor.reset();
  }

  m_in_codec_parameters.reset();
  m_in_time_base = AVRational{};
  m_out_codec_parameters.reset();
  m_out_time_base = AVRational{};
}

const avcodec_parameters_ptr& compat_sink::output_codec_parameters() const
{
  return m_out_codec_parameters;
}

AVRational compat_sink::output_time_base() const
{
  return m_out_time_base;
}
