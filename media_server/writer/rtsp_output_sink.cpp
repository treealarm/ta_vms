#include "writer/rtsp_output_sink.h"

#include <iostream>

rtsp_output_sink::rtsp_output_sink(std::string rtsp_url)
  : m_rtsp_url(std::move(rtsp_url))
{
  m_video_config.media_type = AVMEDIA_TYPE_VIDEO;
  m_audio_config.media_type = AVMEDIA_TYPE_AUDIO;
}

rtsp_output_sink::~rtsp_output_sink()
{
  close();
}

bool rtsp_output_sink::init(const std::vector<stream_config>& streams)
{
  const auto video_config = m_video_config;
  const auto audio_config = m_audio_config;

  close();

  m_video_config = video_config;
  m_audio_config = audio_config;

  if (streams.empty() || m_rtsp_url.empty()) {
    return false;
  }

  if (avformat_alloc_output_context2(&m_ctx, nullptr, "rtsp", m_rtsp_url.c_str()) < 0 || !m_ctx) {
    std::cerr << "failed to allocate rtsp output context\n";
    return false;
  }

  m_streams.clear();
  m_streams.reserve(streams.size());

  for (const auto& stream : streams) {
    if (!stream.codec_parameters) {
      continue;
    }

    AVStream* out_stream = avformat_new_stream(m_ctx, nullptr);
    if (!out_stream) {
      std::cerr << "failed to create rtsp output stream\n";
      close();
      return false;
    }

    if (avcodec_parameters_copy(out_stream->codecpar, stream.codec_parameters.get()) < 0) {
      std::cerr << "failed to copy codec parameters\n";
      close();
      return false;
    }

    out_stream->time_base = stream.time_base.num > 0 ? stream.time_base : AVRational{ 1, 90000 };

    stream_state state;
    state.media_type = stream.media_type;
    state.out_stream = out_stream;
    state.input_time_base = stream.time_base;
    m_streams.push_back(state);
  }

  if (m_streams.empty()) {
    close();
    return false;
  }

  AVDictionary* opts = nullptr;
  av_dict_set(&opts, "rtsp_transport", "tcp", 0);
  av_dict_set(&opts, "timeout", "5000000", 0);
  av_dict_set(&opts, "rw_timeout", "5000000", 0);

  if (avformat_write_header(m_ctx, &opts) < 0) {
    std::cerr << "failed to write rtsp header\n";
    av_dict_free(&opts);
    close();
    return false;
  }

  av_dict_free(&opts);

  m_is_initialized = true;
  m_wait_keyframe = true;
  m_consecutive_write_errors = 0;
  return true;
}

rtsp_output_sink::stream_state* rtsp_output_sink::find_stream(AVMediaType media_type)
{
  for (auto& stream : m_streams) {
    if (stream.media_type == media_type) {
      return &stream;
    }
  }

  return nullptr;
}

bool rtsp_output_sink::update_stream_config_from_packet(const std::shared_ptr<media_packet>& media)
{
  if (!media || !media->codec_parameters) {
    return false;
  }

  stream_config* config = nullptr;
  if (media->media_type == AVMEDIA_TYPE_VIDEO) {
    config = &m_video_config;
  } else if (media->media_type == AVMEDIA_TYPE_AUDIO) {
    config = &m_audio_config;
  } else {
    return false;
  }

  const bool changed = !config->codec_parameters ||
    config->codec_parameters->codec_id != media->codec_parameters->codec_id ||
    config->codec_parameters->codec_type != media->codec_parameters->codec_type ||
    config->time_base.num != media->time_base.num ||
    config->time_base.den != media->time_base.den;

  config->codec_parameters = media->codec_parameters;
  config->time_base = media->time_base;

  return changed;
}

std::vector<rtsp_output_sink::stream_config> rtsp_output_sink::collect_known_streams() const
{
  std::vector<stream_config> streams;

  if (m_video_config.codec_parameters) {
    streams.push_back(m_video_config);
  }

  if (m_audio_config.codec_parameters) {
    streams.push_back(m_audio_config);
  }

  return streams;
}

void rtsp_output_sink::on_packet(const std::shared_ptr<media_packet>& media)
{
  if (!media || !media->packet) {
    return;
  }

  const bool config_changed = update_stream_config_from_packet(media);

  if (!m_is_initialized) {
    const auto streams = collect_known_streams();
    if (streams.empty() || !init(streams)) {
      return;
    }
  } else {
    auto* existing_stream = find_stream(media->media_type);
    if (!existing_stream || config_changed) {
      const auto streams = collect_known_streams();
      if (streams.empty() || !init(streams)) {
        return;
      }
    }
  }

  auto* stream = find_stream(media->media_type);
  if (!stream || !stream->out_stream) {
    return;
  }

  AVPacket* pkt = media->packet.get();

  if (stream->media_type == AVMEDIA_TYPE_VIDEO && m_wait_keyframe) {
    if (!(pkt->flags & AV_PKT_FLAG_KEY)) {
      return;
    }

    m_wait_keyframe = false;
  }

  av_packet_rescale_ts(pkt, media->time_base, stream->out_stream->time_base);

  if (pkt->dts == AV_NOPTS_VALUE && pkt->pts != AV_NOPTS_VALUE) {
    pkt->dts = pkt->pts;
  }
  if (pkt->pts == AV_NOPTS_VALUE && pkt->dts != AV_NOPTS_VALUE) {
    pkt->pts = pkt->dts;
  }

  if (stream->last_dts != AV_NOPTS_VALUE && pkt->dts != AV_NOPTS_VALUE && pkt->dts <= stream->last_dts) {
    pkt->dts = stream->last_dts + 1;
  }

  if (stream->last_pts != AV_NOPTS_VALUE && pkt->pts != AV_NOPTS_VALUE && pkt->pts <= stream->last_pts) {
    pkt->pts = stream->last_pts + 1;
  }

  if (pkt->pts != AV_NOPTS_VALUE && pkt->dts != AV_NOPTS_VALUE && pkt->pts < pkt->dts) {
    pkt->pts = pkt->dts;
  }

  pkt->stream_index = stream->out_stream->index;

  stream->last_dts = pkt->dts;
  stream->last_pts = pkt->pts;

  if (av_interleaved_write_frame(m_ctx, pkt) < 0) {
    ++m_consecutive_write_errors;

    constexpr int critical_write_error_threshold = 3;
    std::cerr << "failed to write packet to rtsp output (consecutive errors: "
      << m_consecutive_write_errors << ")\n";

    if (m_consecutive_write_errors >= critical_write_error_threshold) {
      std::cerr << "rtsp output sink watchdog: closing broken output context for reinit\n";
      close();
    }

    return;
  }

  m_consecutive_write_errors = 0;
}

void rtsp_output_sink::close()
{
  if (!m_ctx) {
    m_streams.clear();
    m_is_initialized = false;
    m_wait_keyframe = true;
    m_consecutive_write_errors = 0;
    return;
  }

  if (m_is_initialized) {
    av_write_trailer(m_ctx);
  }

  if (!(m_ctx->oformat->flags & AVFMT_NOFILE) && m_ctx->pb) {
    avio_closep(&m_ctx->pb);
  }

  avformat_free_context(m_ctx);
  m_ctx = nullptr;
  m_streams.clear();
  m_is_initialized = false;
  m_wait_keyframe = true;
  m_consecutive_write_errors = 0;
}
