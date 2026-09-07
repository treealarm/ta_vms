#include "writer/compat_sink.h"

#include <cstdlib>
#include <string_view>

#include "interfaces/media_packet.h"
#include "logger/logger.h"
#include "writer/processors/audio_copy_processor.h"
#include "writer/processors/audio_transcode_processor.h"
#include "writer/processors/sve_transcode_processor.h"
#include "writer/processors/video_copy_processor.h"
#include "writer/processors/video_transcode_processor.h"


#include "av_utils/need_reinit.h"


compat_sink::~compat_sink()
{
  close();
}

bool compat_sink::init(const avcodec_parameters_ptr& codec_parameters, AVRational time_base, AVRational frame_rate)
{
  close();

  std::lock_guard<std::mutex> lock(m_mutex);

  // Candidates in preference order. Video transcode offers two: the hardware encoder through sve,
  // and the CPU one behind it. The second is not a duplicate of sve's own software backend -- it
  // asks libx264 for `tune=zerolatency` where sve does not, which on a 1080p25 stream is 4 frames
  // in flight instead of 34.
  for (auto& candidate : create_processors(codec_parameters))
  {
    if (!candidate)
    {
      continue;
    }

    if (candidate->init(codec_parameters, time_base, frame_rate,
          [this](const std::shared_ptr<media_packet>& media) { emit_packet(media); }))
    {
      m_processor = std::move(candidate);
      break;
    }
  }

  if (!m_processor)
  {
    logger::instance().warning("compat_sink: no stream processor could be initialized");
    return false;
  }

  m_in_codec_parameters = codec_parameters;
  m_in_time_base = time_base;
  m_out_codec_parameters = m_processor->output_codec_parameters();
  m_out_time_base = m_processor->output_time_base();
  return true;
}

std::vector<std::unique_ptr<stream_processor>> compat_sink::create_processors(
  const avcodec_parameters_ptr& codec_parameters) const
{
  std::vector<std::unique_ptr<stream_processor>> candidates;

  if (!codec_parameters) 
  {
    return candidates;
  }

  if (codec_parameters->codec_type == AVMEDIA_TYPE_VIDEO)
  {
    if (codec_parameters->codec_id == AV_CODEC_ID_H264) 
    {
      candidates.push_back(std::make_unique<video_copy_processor>());
      return candidates;
    }

    // H.265 is the one other codec WebRTC can carry, so it is the only one the router's setting
    // governs. Everything else a camera might send -- MJPEG above all -- has no WebRTC
    // representation at all, and copying it through would publish a stream no browser can play.
    // Those keep being transcoded whatever the setting says.
    if (codec_parameters->codec_id == AV_CODEC_ID_HEVC && !m_transcode_hevc)
    {
      candidates.push_back(std::make_unique<video_copy_processor>());
      return candidates;
    }

    // Hardware first. sve_transcode_processor declines -- returns false from init -- when no
    // hardware backend is asked for or none opens, and the CPU processor behind it is the one
    // this path has always used.
    candidates.push_back(std::make_unique<sve_transcode_processor>());
    candidates.push_back(std::make_unique<video_transcode_processor>());
    return candidates;
  }

  if (codec_parameters->codec_type == AVMEDIA_TYPE_AUDIO) 
  {
    if (codec_parameters->codec_id == AV_CODEC_ID_OPUS) 
    {
      candidates.push_back(std::make_unique<audio_copy_processor>());
      return candidates;
    }

    candidates.push_back(std::make_unique<audio_transcode_processor>());
  }

  return candidates;
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
    if (!init(media->codec_parameters, media->time_base, media->frame_rate))
    {
      logger::instance().warning("compat_sink: reinitialization failed, dropping packet");
      return;
    }

  }

  std::lock_guard<std::mutex> lock(m_mutex);

  if (!m_processor)
  {
    logger::instance().warning("compat_sink: processor is not initialized, dropping packet");
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

    sink->on_packet(media);
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

std::string compat_sink::video_encoder_name() const
{
  std::lock_guard<std::mutex> lock(m_mutex);
  return m_processor ? m_processor->encoder_name() : std::string{};
}
