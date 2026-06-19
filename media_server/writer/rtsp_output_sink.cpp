#include <format>

#include "writer/rtsp_output_sink.h"
#include "av_utils/need_reinit.h"
#include "logger/logger.h"



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
  AVFormatContext* ctx = nullptr;

  if (avformat_alloc_output_context2(&ctx, nullptr, "rtsp", m_rtsp_url.c_str()) < 0 || !ctx) 
  {
    logger::instance().error("failed to allocate rtsp output context");
    return false;
  }
  m_ctx.reset(ctx);

  m_streams.clear();
  m_streams.reserve(streams.size());

  for (const auto& stream : streams) {
    if (!stream.codec_parameters) {
      continue;
    }

    AVStream* out_stream = avformat_new_stream(m_ctx.get(), nullptr);
    if (!out_stream) {
      logger::instance().error("failed to create rtsp output stream");
      close();
      return false;
    }

    if (avcodec_parameters_copy(out_stream->codecpar, stream.codec_parameters.get()) < 0) {
      logger::instance().error("failed to copy codec parameters");
      close();
      return false;
    }

    out_stream->time_base = m_master_time_base;

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
  av_dict_set(&opts, "max_interleave_delta", "1000000", 0);//10000000 (10 seconds) 1000000 (1 seconds)

  if (avformat_write_header(m_ctx.get(), &opts) < 0) {
    logger::instance().error("failed to write rtsp header");
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

bool rtsp_output_sink::try_init(const std::vector<stream_config>& streams)
{
  const auto now = std::chrono::steady_clock::now();
  if (now < m_next_init_attempt) {
    return false;
  }

  if (init(streams)) {
    return true;
  }

  m_next_init_attempt = now + kInitRetryInterval;
  return false;
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
  if (!media || !media->codec_parameters) 
  {
    return false;
  }

  stream_config* config = nullptr;
  if (media->media_type == AVMEDIA_TYPE_VIDEO) 
  {
    config = &m_video_config;
  } 
  else if (media->media_type == AVMEDIA_TYPE_AUDIO) 
  {
    config = &m_audio_config;
  } 
  else
  {
    return false;
  }

  const bool changed =
    av_utils::need_reinit(
      config->codec_parameters.get(),
      config->time_base,
      media->codec_parameters.get(),
      media->time_base
    );

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

void rtsp_output_sink::on_packet(const std::shared_ptr<media_packet>& in_media)
{
  if (!in_media || !in_media->packet) {
    return;
  }

  auto media = clone_media_packet(in_media);
  if (!media)
    return;

  const bool config_changed = update_stream_config_from_packet(media);

  if (!m_is_initialized) {
    const auto streams = collect_known_streams();
    if (streams.empty() || !try_init(streams)) {
      return;
    }
  }
  else {
    auto* existing_stream = find_stream(media->media_type);
    if (!existing_stream || config_changed) {
      const auto streams = collect_known_streams();
      if (streams.empty() || !try_init(streams)) {
        return;
      }
    }
  }

  auto* stream = find_stream(media->media_type);
  if (!stream || !stream->out_stream) {
    return;
  }

  AVPacket* pkt = media->packet.get();

  if (stream->media_type == AVMEDIA_TYPE_VIDEO && m_wait_keyframe)
  {
    if (!(pkt->flags & AV_PKT_FLAG_KEY))
    {
      return; // ждём keyframe
    }
    m_wait_keyframe = false;

    if (pkt->pts <= 0)
    {
      m_last_master_pts = 0;
    }
    else
    {
      m_last_master_pts = pkt->pts;
    }
  }

  // --- 1. Рескейл в out_stream->time_base (как раньше) ---
  av_packet_rescale_ts(pkt, media->time_base, stream->out_stream->time_base);


  // --- 2. Подгонка PTS/DTS под master (видео) ---
  if (stream->media_type != AVMEDIA_TYPE_VIDEO)
  {
    const int64_t threshold = 180; // ~2 мс в timebase 1/90000
    int64_t offset = m_last_master_pts - pkt->pts;

    if (std::abs(offset) > threshold)
    {
      // сильный рассинхрон → подгоняем
      pkt->pts = m_last_master_pts;
      pkt->dts = m_last_master_pts;
    }
  }

  // --- 3. Восстановление отсутствующих PTS/DTS ---
  int64_t step = pkt->duration > 0 ? pkt->duration : 1;

  if (pkt->pts <= 0 && pkt->dts <= 0) {
    int64_t next = (stream->last_dts != AV_NOPTS_VALUE) ? stream->last_dts + step : 0;
    pkt->pts = pkt->dts = next;
  }
  else if (pkt->pts <= 0) {
    pkt->pts = pkt->dts;
  }
  else if (pkt->dts <= 0) {
    pkt->dts = pkt->pts;
  }

  // --- 4. Монотонность внутри потока ---
  if (stream->last_dts != AV_NOPTS_VALUE && pkt->dts <= stream->last_dts) {
    pkt->dts = stream->last_dts + step;
  }
  if (stream->last_pts != AV_NOPTS_VALUE && pkt->pts <= stream->last_pts) {
    pkt->pts = stream->last_pts + step;
  }

  // --- 5. PTS >= DTS ---
  if (pkt->pts < pkt->dts) {
    pkt->pts = pkt->dts;
  }


  pkt->stream_index = stream->out_stream->index;

  // --- 6. Обновляем last_pts/last_dts ---
  stream->last_dts = pkt->dts;
  stream->last_pts = pkt->pts;

  // --- 7. Обновляем master PTS если это видео ---
  if (stream->media_type == AVMEDIA_TYPE_VIDEO) {
    m_last_master_pts = pkt->pts;
  }

  if (pkt->pts < 0)
  {
    logger::instance().warning("rtsp output sink: pkt->pts < 0");
  }
  // --- 5. Запись пакета ---
  const int write_result = av_interleaved_write_frame(m_ctx.get(), pkt);
  if (write_result < 0) {
    ++m_consecutive_write_errors;

    char errbuf[256] = {0};
    av_strerror(write_result, errbuf, sizeof(errbuf));
    constexpr int critical_write_error_threshold = 3;
    logger::instance().error(std::format(
      "failed to write packet to rtsp output (consecutive errors: {}): code={} msg={} media_type={}",
      m_consecutive_write_errors, write_result, errbuf, (int)stream->media_type));

    if (m_consecutive_write_errors >= critical_write_error_threshold) {
      logger::instance().warning("rtsp output sink watchdog: closing broken output context for reinit");
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
    av_write_trailer(m_ctx.get());
  }

  if (!(m_ctx->oformat->flags & AVFMT_NOFILE) && m_ctx->pb) {
    avio_closep(&m_ctx->pb);
  }

  m_ctx.reset();

  m_streams.clear();
  m_is_initialized = false;
  m_wait_keyframe = true;
  m_consecutive_write_errors = 0;
}
