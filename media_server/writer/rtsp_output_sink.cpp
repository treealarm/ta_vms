#include "writer/rtsp_output_sink.h"

#include <iostream>

rtsp_output_sink::rtsp_output_sink(std::string rtsp_url)
  : m_rtsp_url(std::move(rtsp_url))
{
}

rtsp_output_sink::~rtsp_output_sink()
{
  close();
}

bool rtsp_output_sink::init(const avcodec_parameters_ptr& codec_parameters, AVRational time_base)
{
  close();

  if (!codec_parameters || m_rtsp_url.empty())
    return false;

  if (avformat_alloc_output_context2(&m_ctx, nullptr, "rtsp", m_rtsp_url.c_str()) < 0 || !m_ctx) {
    std::cerr << "failed to allocate rtsp output context\n";
    return false;
  }

  m_out_stream = avformat_new_stream(m_ctx, nullptr);
  if (!m_out_stream) {
    std::cerr << "failed to create rtsp output stream\n";
    close();
    return false;
  }

  if (avcodec_parameters_copy(m_out_stream->codecpar, codec_parameters.get()) < 0) {
    std::cerr << "failed to copy codec parameters\n";
    close();
    return false;
  }

  m_out_stream->time_base = time_base.num > 0 ? time_base : AVRational{ 1,25 };

  AVDictionary* opts = nullptr;
  av_dict_set(&opts, "rtsp_transport", "tcp", 0);

  if (avformat_write_header(m_ctx, &opts) < 0) {
    std::cerr << "failed to write rtsp header\n";
    av_dict_free(&opts);
    close();
    return false;
  }

  av_dict_free(&opts);

  m_is_initialized = true;
  return true;
}
void rtsp_output_sink::on_packet(const std::shared_ptr<media_packet>& media)
{
  if (!m_is_initialized || !media || !media->packet || !m_out_stream) {
    return;
  }

  AVPacket* pkt = media->packet.get();
  av_packet_rescale_ts(pkt, media->time_base, m_out_stream->time_base);
  pkt->stream_index = m_out_stream->index;

  if (av_interleaved_write_frame(m_ctx, pkt) < 0) {
    std::cerr << "failed to write packet to rtsp output\n";
  }
}

void rtsp_output_sink::close()
{
  if (!m_ctx) {
    m_out_stream = nullptr;
    m_is_initialized = false;
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
  m_out_stream = nullptr;
  m_is_initialized = false;
}
