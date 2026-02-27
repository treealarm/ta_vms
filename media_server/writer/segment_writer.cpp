#include "writer/segment_writer.h"

#include <iostream>
#include <format>
#include <chrono>

segment_writer::segment_writer(const std::string& base_dir,
  const std::string& camera_id,
  int segment_seconds)
  : m_base_dir(base_dir),
  m_camera_id(camera_id),
  m_segment_seconds(segment_seconds)
{
}

bool segment_writer::init(AVStream* in_stream)
{
  m_in_stream = in_stream;
  std::filesystem::create_directories(m_base_dir / m_camera_id);
  return true;
}

void segment_writer::open_new_segment()
{
  close_segment();

  auto path = m_base_dir / m_camera_id / make_filename();

  if (avformat_alloc_output_context2(
    &m_ctx, nullptr, nullptr, path.string().c_str()) < 0)
  {
    std::cerr << "avformat_alloc_output_context2 failed\n";
    m_ctx = nullptr;
    return;
  }

  m_out_stream = avformat_new_stream(m_ctx, nullptr);
  avcodec_parameters_copy(
    m_out_stream->codecpar, m_in_stream->codecpar);

  m_out_stream->time_base = m_in_stream->time_base;

  if (!(m_ctx->oformat->flags & AVFMT_NOFILE)) {
    if (avio_open(&m_ctx->pb,
      path.string().c_str(),
      AVIO_FLAG_WRITE) < 0)
    {
      std::cerr << "avio_open failed\n";
      avformat_free_context(m_ctx);
      m_ctx = nullptr;
      return;
    }
  }

  if (avformat_write_header(m_ctx, nullptr) < 0) {
    std::cerr << "avformat_write_header failed\n";
    avio_closep(&m_ctx->pb);
    avformat_free_context(m_ctx);
    m_ctx = nullptr;
    return;
  }
  m_segment_start_time = std::chrono::steady_clock::now();
  m_pts = 0;
}

void segment_writer::close_segment()
{
  if (!m_ctx)
    return;

  av_write_trailer(m_ctx);

  if (!(m_ctx->oformat->flags & AVFMT_NOFILE))
    avio_closep(&m_ctx->pb);

  avformat_free_context(m_ctx);
  m_ctx = nullptr;
  m_out_stream = nullptr;
}

void segment_writer::on_packet(const std::shared_ptr<media_packet>& media)
{
  if (!media || !media->packet)
    return;

  AVPacket* pkt = media->packet.get();

  // стартуем ТОЛЬКО с keyframe
  if (!m_ctx) {
    if (!(pkt->flags & AV_PKT_FLAG_KEY))
      return;

    open_new_segment();
    if (!m_ctx)
      return;
  }

  auto elapsed = std::chrono::steady_clock::now() - m_segment_start_time;

  // 🔪 режем ТОЛЬКО по keyframe
  if (elapsed >= m_segment_seconds &&
    (pkt->flags & AV_PKT_FLAG_KEY))
  {
    open_new_segment();
    if (!m_ctx)
      return;
  }

  AVRational tb = m_out_stream->time_base;

  int64_t frame_duration = av_rescale_q(1, AVRational{ 1,25 }, tb); ;
  
  av_packet_rescale_ts(pkt, m_in_stream->time_base, m_out_stream->time_base);

  if (pkt->duration <= 0)
  {
    pkt->duration = frame_duration;
  }
  else
  {
    frame_duration = pkt->duration;
  }

    pkt->dts = m_pts;
    pkt->pts = m_pts;

  m_pts += frame_duration;
  

  pkt->stream_index = m_out_stream->index;

  if (av_interleaved_write_frame(m_ctx, pkt) < 0) {
    std::cerr << "write_frame failed\n";
  }
}

std::string segment_writer::make_filename() const
{
  using namespace std::chrono;
  auto now = floor<seconds>(system_clock::now());
  return std::format("{:%Y-%m-%d_%H-%M-%S}.mkv", now);
}

void segment_writer::close()
{
  close_segment();
}