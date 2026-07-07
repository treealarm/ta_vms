#include <chrono>
#include <format>
#include <cstdlib>

#include "rtsp_timeline_cache.h"
#include "writer/rtsp_output_sink.h"
#include "av_utils/need_reinit.h"
#include "logger/logger.h"

namespace {
avcodec_parameters_ptr copy_codec_parameters(const AVCodecParameters* source)
{
  if (!source) {
    return nullptr;
  }
  avcodec_parameters_ptr copy(
    avcodec_parameters_alloc(),
    [](AVCodecParameters* params) { avcodec_parameters_free(&params); });
  if (!copy || avcodec_parameters_copy(copy.get(), source) < 0) {
    return nullptr;
  }
  return copy;
}

bool codecpar_ready_for_mux(const AVCodecParameters* par)
{
  if (!par || par->codec_type == AVMEDIA_TYPE_UNKNOWN) {
    return false;
  }
  if (par->codec_type == AVMEDIA_TYPE_VIDEO && par->width <= 0 && par->height <= 0) {
    return false;
  }
  return true;
}
}  // namespace

rtsp_output_sink::rtsp_output_sink(std::string rtsp_url)
  : m_rtsp_url(std::move(rtsp_url))
{
  m_video_config.media_type = AVMEDIA_TYPE_VIDEO;
  m_audio_config.media_type = AVMEDIA_TYPE_AUDIO;
}

rtsp_output_sink::~rtsp_output_sink()
{
  close_unlocked();
}

bool rtsp_output_sink::init(const std::vector<stream_config>& streams)
{
  const auto video_config = m_video_config;
  const auto audio_config = m_audio_config;

  close_unlocked();

  m_video_config = video_config;
  m_audio_config = audio_config;
  m_last_master_pts = AV_NOPTS_VALUE;

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
  bool restored_master_timing = false;

  for (const auto& stream : streams) {
    if (!stream.codec_parameters) {
      continue;
    }
    if (!codecpar_ready_for_mux(stream.codec_parameters.get())) {
      logger::instance().warning("rtsp output sink: codec parameters not ready for mux");
      continue;
    }

    AVStream* out_stream = avformat_new_stream(m_ctx.get(), nullptr);
    if (!out_stream) {
      logger::instance().error("failed to create rtsp output stream");
      close_unlocked();
      return false;
    }

    if (avcodec_parameters_copy(out_stream->codecpar, stream.codec_parameters.get()) < 0) {
      logger::instance().error("failed to copy codec parameters");
      close_unlocked();
      return false;
    }
    out_stream->codecpar->codec_tag = 0;

    out_stream->time_base = m_master_time_base;

    stream_state state;
    state.media_type = stream.media_type;
    state.out_stream = out_stream;
    state.input_time_base = stream.time_base;
    const bool can_restore_timing =
      state.media_type == AVMEDIA_TYPE_VIDEO || !m_video_config.codec_parameters ||
      restored_master_timing;
    const bool restored_timing = can_restore_timing
      && rtsp_timeline_cache::instance().restore(
        m_rtsp_url, state.media_type, state.last_dts, state.last_pts);
    if (restored_timing) {
      state.pending_timestamp_offset = true;
      if (state.media_type == AVMEDIA_TYPE_VIDEO) {
        m_last_master_pts = state.last_pts;
        restored_master_timing = true;
      }
    }
    m_streams.push_back(state);
  }

  if (m_streams.empty()) {
    close_unlocked();
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
    close_unlocked();
    return false;
  }

  av_dict_free(&opts);

  m_is_initialized = true;
  m_wait_keyframe = true;
  m_consecutive_write_errors = 0;

  bool has_video = false;
  bool has_audio = false;
  for (const auto& stream : m_streams) {
    has_video = has_video || stream.media_type == AVMEDIA_TYPE_VIDEO;
    has_audio = has_audio || stream.media_type == AVMEDIA_TYPE_AUDIO;
  }
  logger::instance().info(std::format(
    "rtsp output sink initialized: video={} audio={}", has_video, has_audio));
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

  auto copied = copy_codec_parameters(media->codec_parameters.get());
  if (!copied) {
    return changed;
  }
  config->codec_parameters = std::move(copied);
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

  std::lock_guard<std::mutex> lock(m_mutex);

  auto media = clone_media_packet(in_media);
  if (!media)
    return;

  const bool config_changed = update_stream_config_from_packet(media);

  if (!m_is_initialized) {
    const auto streams = collect_known_streams();
    if (streams.empty() || !init(streams)) {
      return;
    }
  }
  else {
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

  if (stream->media_type == AVMEDIA_TYPE_VIDEO && m_wait_keyframe)
  {
    if (!(pkt->flags & AV_PKT_FLAG_KEY))
    {
      return; // ждём keyframe
    }
    m_wait_keyframe = false;
  }

  // --- 1. Рескейл в out_stream->time_base (как раньше) ---
  av_packet_rescale_ts(pkt, media->time_base, stream->out_stream->time_base);

  int64_t step = pkt->duration > 0 ? pkt->duration : 1;

  // File loop: source PTS restarts near zero; bump forward so muxer DTS stays monotonic.
  // Live RTSP never jumps backward by ~10 s — threshold avoids false positives.
  constexpr int64_t kLoopBackwardThreshold = 900000; // ~10 s at 1/90000
  if (stream->last_pts != AV_NOPTS_VALUE
    && pkt->pts != AV_NOPTS_VALUE
    && pkt->pts + kLoopBackwardThreshold < stream->last_pts)
  {
    const int64_t base_ts = pkt->pts;
    const int64_t target_next = stream->last_pts + step;
    const int64_t timestamp_offset = target_next - base_ts;

    if (pkt->pts != AV_NOPTS_VALUE) {
      pkt->pts += timestamp_offset;
    }
    if (pkt->dts != AV_NOPTS_VALUE) {
      pkt->dts += timestamp_offset;
    }
    stream->pending_timestamp_offset = false;
  }

  if (stream->pending_timestamp_offset
    && (pkt->pts != AV_NOPTS_VALUE || pkt->dts != AV_NOPTS_VALUE)) {
    const int64_t base_ts = pkt->pts != AV_NOPTS_VALUE ? pkt->pts : pkt->dts;
    const int64_t target_next = stream->last_pts != AV_NOPTS_VALUE ? stream->last_pts + step : base_ts;
    const int64_t timestamp_offset = target_next - base_ts;

    if (pkt->pts != AV_NOPTS_VALUE) {
      pkt->pts += timestamp_offset;
    }
    if (pkt->dts != AV_NOPTS_VALUE) {
      pkt->dts += timestamp_offset;
    }

    stream->pending_timestamp_offset = false;
  }

  // --- 2. Восстановление отсутствующих PTS/DTS ---
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

  // --- 3. Подгонка PTS/DTS под master (видео) ---
  if (stream->media_type != AVMEDIA_TYPE_VIDEO && m_last_master_pts != AV_NOPTS_VALUE)
  {
    const int64_t threshold = 180; // ~2 ms at 1/90000
    int64_t offset = m_last_master_pts - pkt->pts;

    if (std::abs(offset) > threshold)
    {
      pkt->pts = m_last_master_pts;
      pkt->dts = m_last_master_pts;
    }
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

  stream->last_dts = pkt->dts;
  stream->last_pts = pkt->pts;

  if (stream->media_type == AVMEDIA_TYPE_VIDEO) {
    m_last_master_pts = pkt->pts;
  }

  if (pkt->pts < 0)
  {
    logger::instance().warning("rtsp output sink: pkt->pts < 0");
  }

  // DIAG: gap since the previous video packet handed to the muxer and time spent
  // inside av_interleaved_write_frame. A large gap with a smooth reader means the
  // stall is between transcode and mux; a long write means MediaMTX backpressure.
  const auto write_started_at = std::chrono::steady_clock::now();
  if (stream->media_type == AVMEDIA_TYPE_VIDEO)
  {
    if (m_last_video_write_at != std::chrono::steady_clock::time_point{})
    {
      const auto gap_ms = std::chrono::duration_cast<std::chrono::milliseconds>(
        write_started_at - m_last_video_write_at).count();
      // Source jitter up to a few hundred ms is normal; flag only real stalls.
      if (gap_ms > 1000)
      {
        const auto audio_ms = m_last_audio_write_at == std::chrono::steady_clock::time_point{}
          ? -1
          : std::chrono::duration_cast<std::chrono::milliseconds>(
              write_started_at - m_last_audio_write_at).count();
        logger::instance().warning(std::format(
          "DIAG rtsp_output_sink: video mux gap={}ms key={} since_last_audio={}ms",
          gap_ms, (int)((pkt->flags & AV_PKT_FLAG_KEY) != 0), audio_ms));
      }
    }
    m_last_video_write_at = write_started_at;
  }
  else if (stream->media_type == AVMEDIA_TYPE_AUDIO)
  {
    m_last_audio_write_at = write_started_at;
  }

  const int write_result = av_interleaved_write_frame(m_ctx.get(), pkt);
  const auto write_ms = std::chrono::duration_cast<std::chrono::milliseconds>(
    std::chrono::steady_clock::now() - write_started_at).count();
  if (write_ms > 200)
    logger::instance().warning(std::format(
      "DIAG rtsp_output_sink: slow mux write={}ms type={} key={}",
      write_ms, (int)stream->media_type, (int)((pkt->flags & AV_PKT_FLAG_KEY) != 0)));

  if (write_result < 0) {
    ++m_consecutive_write_errors;

    constexpr int critical_write_error_threshold = 3;
    logger::instance().error(std::format(
      "failed to write packet to rtsp output (consecutive errors: {})",
      m_consecutive_write_errors));

    if (m_consecutive_write_errors >= critical_write_error_threshold) {
      logger::instance().warning("rtsp output sink watchdog: closing broken output context for reinit");
      close_unlocked();
    }
    return;
  }

  m_consecutive_write_errors = 0;
  rtsp_timeline_cache::instance().persist(
    m_rtsp_url, stream->media_type, stream->last_dts, stream->last_pts);
}

void rtsp_output_sink::request_video_keyframe()
{
  std::lock_guard<std::mutex> lock(m_mutex);
  m_wait_keyframe = true;
}

void rtsp_output_sink::close()
{
  std::lock_guard<std::mutex> lock(m_mutex);
  close_unlocked();
}

void rtsp_output_sink::close_unlocked()
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
