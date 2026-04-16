#include "writer/sync_packet_transcoder.h"

#include "av_utils/need_reinit.h"
#include "writer/processors/audio_copy_processor.h"
#include "writer/processors/audio_transcode_processor.h"
#include "writer/processors/video_copy_processor.h"
#include "writer/processors/video_transcode_processor.h"

sync_packet_transcoder::sync_packet_transcoder(codec_config config)
  : m_config(config)
{
}

sync_packet_transcoder::~sync_packet_transcoder()
{
  close();
}

std::unique_ptr<stream_processor> sync_packet_transcoder::create_processor(const avcodec_parameters_ptr& codec_parameters) const
{
  if (!codec_parameters)
  {
    return nullptr;
  }

  if (codec_parameters->codec_type == AVMEDIA_TYPE_VIDEO)
  {
    if (codec_parameters->codec_id == m_config.video_codec ||
        (m_config.allow_hevc_video_passthrough && codec_parameters->codec_id == AV_CODEC_ID_H265))
    {
      return std::make_unique<video_copy_processor>();
    }

    return std::make_unique<video_transcode_processor>(m_config.video_codec);
  }

  if (codec_parameters->codec_type == AVMEDIA_TYPE_AUDIO)
  {
    if (m_config.allow_all_audio_passthrough)
    {
      return std::make_unique<audio_copy_processor>();
    }

    if (codec_parameters->codec_id == m_config.audio_codec)
    {
      return std::make_unique<audio_copy_processor>();
    }

    return std::make_unique<audio_transcode_processor>(m_config.audio_codec);
  }

  return nullptr;
}

bool sync_packet_transcoder::should_reinitialize(const processor_state& state, const std::shared_ptr<media_packet>& media) const
{
  if (!media || !media->codec_parameters)
  {
    return false;
  }

  if (!state.processor || !state.in_codec_parameters)
  {
    return true;
  }

  return av_utils::need_reinit(
    state.in_codec_parameters.get(),
    state.in_time_base,
    media->codec_parameters.get(),
    media->time_base
  );
}

bool sync_packet_transcoder::init_state(processor_state& state, const std::shared_ptr<media_packet>& media)
{
  auto processor = create_processor(media->codec_parameters);
  if (!processor)
  {
    return false;
  }

  if (!processor->init(media->codec_parameters, media->time_base, [this, &state](const std::shared_ptr<media_packet>& out_media)
    {
      if (!out_media)
      {
        return;
      }

      out_media->stream_index = state.current_stream_index;
      out_media->media_type = state.current_media_type;
      out_media->frame_rate = state.current_frame_rate;
      out_media->sample_rate = state.current_sample_rate;
      m_pending_packets.push_back(out_media);
    }))
  {
    return false;
  }

  state.processor = std::move(processor);
  state.in_codec_parameters = media->codec_parameters;
  state.in_time_base = media->time_base;

  return true;
}

sync_packet_transcoder::processor_state* sync_packet_transcoder::state_for_media_type(AVMediaType media_type)
{
  if (media_type == AVMEDIA_TYPE_VIDEO)
  {
    return &m_video_state;
  }

  if (media_type == AVMEDIA_TYPE_AUDIO)
  {
    return &m_audio_state;
  }

  return nullptr;
}

std::shared_ptr<media_packet> sync_packet_transcoder::transcode_packet(const std::shared_ptr<media_packet>& media)
{
  std::lock_guard<std::mutex> lock(m_mutex);

  if (media && media->packet && media->codec_parameters)
  {
    auto* state = state_for_media_type(media->media_type);
    if (state)
    {
      if (should_reinitialize(*state, media))
      {
        if (state->processor)
        {
          state->processor->flush();
          state->processor.reset();
        }

        if (!init_state(*state, media))
        {
          return nullptr;
        }
      }

      state->current_stream_index = media->stream_index;
      state->current_media_type = media->media_type;
      state->current_frame_rate = media->frame_rate;
      state->current_sample_rate = media->sample_rate;

      state->processor->process(media);
    }
    else
    {
      m_pending_packets.push_back(media);
    }
  }

  if (m_pending_packets.empty())
  {
    return nullptr;
  }

  auto out_media = std::move(m_pending_packets.front());
  m_pending_packets.pop_front();
  return out_media;
}

void sync_packet_transcoder::close()
{
  std::lock_guard<std::mutex> lock(m_mutex);

  if (m_video_state.processor)
  {
    m_video_state.processor->flush();
    m_video_state.processor.reset();
  }

  if (m_audio_state.processor)
  {
    m_audio_state.processor->flush();
    m_audio_state.processor.reset();
  }

  m_video_state.in_codec_parameters.reset();
  m_video_state.in_time_base = AVRational{};
  m_video_state.current_stream_index = -1;
  m_video_state.current_media_type = AVMEDIA_TYPE_UNKNOWN;
  m_video_state.current_frame_rate = AVRational{ 0, 1 };
  m_video_state.current_sample_rate = 0;

  m_audio_state.in_codec_parameters.reset();
  m_audio_state.in_time_base = AVRational{};
  m_audio_state.current_stream_index = -1;
  m_audio_state.current_media_type = AVMEDIA_TYPE_UNKNOWN;
  m_audio_state.current_frame_rate = AVRational{ 0, 1 };
  m_audio_state.current_sample_rate = 0;

  m_pending_packets.clear();
}
