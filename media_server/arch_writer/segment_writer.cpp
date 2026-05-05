#include "file_system/recording_storage.h"

#include "segment_writer.h"

#include <format>
#include <optional>
#include <system_error>

#include "grpc_layer/fs_client.h"
#include "logger/logger.h"

segment_writer::segment_writer(
  const std::string& camera_id,
  std::chrono::seconds segment_duration)
  :
  m_camera_id(camera_id),
  m_segment_duration(segment_duration.count() > 0 ? segment_duration : std::chrono::seconds(60)),
  m_packet_transcoder([]
    {
      recording_storage storage;
      sync_packet_transcoder::codec_config config;
      config.video_codec = AV_CODEC_ID_H264;
      config.allow_hevc_video_passthrough = true;
      const bool is_ts = storage.file_ext() == recording_storage::k_ts_ext;
      config.audio_codec = is_ts ? AV_CODEC_ID_AAC : AV_CODEC_ID_OPUS;
      config.allow_all_audio_passthrough = !is_ts;
      return config;
    }())
{
}

segment_writer::~segment_writer()
{
  close();
}

bool segment_writer::ensure_input_stream(const std::shared_ptr<media_packet>& media)
{
  if (!media || !media->codec_parameters || media->stream_index < 0)
  {
    return false;
  }

  if (m_input_streams.contains(media->stream_index))
  {
    return true;
  }

  avcodec_parameters_ptr codec_copy(avcodec_parameters_alloc(),
    [](AVCodecParameters* params) { avcodec_parameters_free(&params); });
  if (!codec_copy)
  {
    return false;
  }

  if (avcodec_parameters_copy(codec_copy.get(), media->codec_parameters.get()) < 0) {
    return false;
  }

  m_input_streams.emplace(media->stream_index,
                          input_stream_info{ std::move(codec_copy), media->time_base, media->media_type });
  return true;
}

void segment_writer::open_new_segment()
{
  m_timing.clear();
  close_segment();

  if (m_input_streams.empty())
  {
    logger::instance().warning("segment writer has no registered streams");
    return;
  }

  const auto now = std::chrono::floor<std::chrono::seconds>(std::chrono::system_clock::now());

  recording_storage storage;

  m_segment_rel_path = storage.make_segment_path(m_camera_id, now);
  const auto path = recording_storage::resolve_storage_base_path() / m_segment_rel_path;
  std::filesystem::create_directories(path.parent_path());
  m_segment_abs_path = path;

  AVFormatContext* ctx = nullptr;

  if (avformat_alloc_output_context2(
    &ctx, nullptr, nullptr, path.string().c_str()) < 0)
  {
    logger::instance().error("avformat_alloc_output_context2 failed");
    return;
  }

  m_ctx.reset(ctx);

  m_output_streams.clear();

  for (const auto& [input_index, stream_info] : m_input_streams)
  {
    AVStream* out_stream = avformat_new_stream(m_ctx.get(), nullptr);
    if (!out_stream)
    {
      logger::instance().error("avformat_new_stream failed");
      m_ctx.reset();
      m_output_streams.clear();
      return;
    }

    if (avcodec_parameters_copy(out_stream->codecpar, stream_info.codec_parameters.get()) < 0)
    {
      logger::instance().error("avcodec_parameters_copy failed");
      m_ctx.reset();
      m_output_streams.clear();
      return;
    }

    out_stream->time_base = stream_info.time_base;
    m_output_streams.emplace(input_index, out_stream);
  }

  if (!(m_ctx->oformat->flags & AVFMT_NOFILE))
  {
    if (avio_open(&m_ctx->pb,
      path.string().c_str(),
      AVIO_FLAG_WRITE) < 0)
    {
      logger::instance().error("avio_open failed");
      m_ctx.reset();
      m_output_streams.clear();
      return;
    }
  }

  if (avformat_write_header(m_ctx.get(), nullptr) < 0)
  {
    logger::instance().error("avformat_write_header failed");
    avio_closep(&m_ctx->pb);
    m_ctx.reset();
    m_output_streams.clear();
    return;
  }

  m_segment_start_time = std::chrono::steady_clock::now();
  m_segment_start_ts = now;

  fs_client::instance().enqueue_ingest_clip(
    m_camera_id,
    m_segment_rel_path.string(),
    m_segment_start_ts,
    std::nullopt,
    0);
}

void segment_writer::close_segment()
{
  if (!m_ctx)
    return;

  av_write_trailer(m_ctx.get());

  if (!(m_ctx->oformat->flags & AVFMT_NOFILE))
    avio_closep(&m_ctx->pb);

  const auto segment_end_ts = std::chrono::system_clock::now();

  m_ctx.reset();
  m_output_streams.clear();

  if (!m_segment_abs_path.empty() && !m_segment_rel_path.empty())
  {
    std::error_code ec;
    const auto size = std::filesystem::file_size(m_segment_abs_path, ec);
    if (ec)
    {
      logger::instance().warning(std::format(
        "failed to read segment size for '{}': {}",
        m_segment_abs_path.string(),
        ec.message()));

      fs_client::instance().enqueue_ingest_clip(
        m_camera_id,
        m_segment_rel_path.string(),
        m_segment_start_ts,
        segment_end_ts,
        0);
    }
    else
    {
      fs_client::instance().enqueue_ingest_clip(
        m_camera_id,
        m_segment_rel_path.string(),
        m_segment_start_ts,
        segment_end_ts,
        size);
    }
  }

  m_segment_abs_path.clear();
  m_segment_rel_path.clear();
  m_segment_start_ts = {};
}

void segment_writer::on_packet(const std::shared_ptr<media_packet>& in_media)
{
  if (!in_media || !in_media->packet)
    return;

  auto media = m_packet_transcoder.transcode_packet(in_media);
  while (media)
  {
    write_packet(media);
    media = m_packet_transcoder.transcode_packet(nullptr);
  }
}

void segment_writer::write_packet(const std::shared_ptr<media_packet>& in_media)
{
  if (!in_media || !in_media->packet || !ensure_input_stream(in_media))
    return;

  auto media = clone_media_packet(in_media);
  if (!media)
    return;

  AVPacket* pkt = media->packet.get();

  if (!m_ctx)
  {
    if (media->media_type != AVMEDIA_TYPE_VIDEO || !(pkt->flags & AV_PKT_FLAG_KEY))
      return;
    open_new_segment();
    if (!m_ctx) return;
  }

  auto elapsed = std::chrono::steady_clock::now() - m_segment_start_time;
  if (elapsed >= m_segment_duration &&
    media->media_type == AVMEDIA_TYPE_VIDEO &&
    (pkt->flags & AV_PKT_FLAG_KEY))
  {
    open_new_segment();
    if (!m_ctx) return;
  }

  auto out_it = m_output_streams.find(media->stream_index);
  if (out_it == m_output_streams.end())
    return;

  AVStream* out_stream = out_it->second;
  auto& timing = m_timing[media->stream_index];

  av_packet_rescale_ts(pkt, media->time_base, out_stream->time_base);

  bool invalid = pkt->pts == AV_NOPTS_VALUE || pkt->dts == AV_NOPTS_VALUE || pkt->pts < 0 || pkt->dts < 0;

  if (!invalid)
  {
    if (timing.first_pts == AV_NOPTS_VALUE) timing.first_pts = pkt->pts;
    if (timing.first_dts == AV_NOPTS_VALUE) timing.first_dts = pkt->dts;

    pkt->pts -= timing.first_pts;
    pkt->dts -= timing.first_dts;

    if (timing.last_pts != AV_NOPTS_VALUE && pkt->pts <= timing.last_pts) pkt->pts = timing.last_pts + 1;
    if (timing.last_dts != AV_NOPTS_VALUE && pkt->dts <= timing.last_dts) pkt->dts = timing.last_dts + 1;
  }
  else
  {
    if (pkt->duration <= 0) pkt->duration = 1;
    int64_t next = (timing.last_pts != AV_NOPTS_VALUE) ? timing.last_pts + pkt->duration : 0;
    pkt->pts = pkt->dts = next;
  }

  timing.last_pts = pkt->pts;
  timing.last_dts = pkt->dts;

  if (pkt->dts > pkt->pts) pkt->dts = pkt->pts;

  pkt->stream_index = out_stream->index;

  if (av_interleaved_write_frame(m_ctx.get(), pkt) < 0)
    logger::instance().error("write_frame failed");
}

void segment_writer::close()
{
  m_packet_transcoder.close();
  close_segment();
}
