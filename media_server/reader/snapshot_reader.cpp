#include "snapshot_reader.h"

#include <iostream>

#include "interfaces/av_deleters.h"

snapshot_reader::snapshot_reader(std::chrono::milliseconds position)
  : m_position(position)
{}

snapshot_reader::~snapshot_reader()
{
  disconnect();
}

bool snapshot_reader::open(const std::string& source)
{
  std::lock_guard lock(m_mutex);
  return connect_current_file(source);
}

AVStream* snapshot_reader::video_stream()
{
  if (m_ctx && m_video_index >= 0)
  {
    return m_ctx->streams[m_video_index];
  }

  return nullptr;
}

std::vector<std::shared_ptr<media_packet>> snapshot_reader::snapshot_packets() const
{
  std::lock_guard lock(m_mutex);

  std::vector<std::shared_ptr<media_packet>> result;
  result.reserve(m_snapshot_packets.size());

  for (const auto& src : m_snapshot_packets)
  {
    auto cloned = clone_media_packet(src);
    if (cloned)
    {
      result.push_back(std::move(cloned));
    }
  }

  return result;
}

void snapshot_reader::disconnect()
{
  if (m_encoder_ctx)
  {
    avcodec_free_context(&m_encoder_ctx);
  }

  m_snapshot_packets.clear();
  m_snapshot_frame.reset();
  m_other_stream_indexes.clear();
  m_ctx.reset();
  m_video_index = -1;
}

bool snapshot_reader::connect_current_file(const std::string& source)
{
  disconnect();

  AVFormatContext* ctx = nullptr;
  if (avformat_open_input(&ctx, source.c_str(), nullptr, nullptr) < 0)
  {
    std::cerr << "Failed to open file source: " << source << "\n";
    return false;
  }

  m_ctx.reset(ctx);

  if (avformat_find_stream_info(m_ctx.get(), nullptr) < 0)
  {
    std::cerr << "Failed to get stream info\n";
    disconnect();
    return false;
  }

  for (unsigned i = 0; i < m_ctx->nb_streams; ++i)
  {
    const auto media_type = m_ctx->streams[i]->codecpar->codec_type;
    if (media_type == AVMEDIA_TYPE_VIDEO && m_video_index < 0)
    {
      m_video_index = static_cast<int>(i);
      continue;
    }

    if (media_type == AVMEDIA_TYPE_VIDEO || media_type == AVMEDIA_TYPE_AUDIO)
    {
      m_other_stream_indexes.push_back(static_cast<int>(i));
    }
  }

  if (m_video_index < 0)
  {
    std::cerr << "No video stream\n";
    disconnect();
    return false;
  }

  if (!decode_to_snapshot_packets(m_position))
  {
    return false;
  }

  build_placeholder_packets();
  return true;
}

bool snapshot_reader::decode_to_snapshot_packets(std::chrono::milliseconds position)
{
  if (!m_ctx || m_video_index < 0)
  {
    return false;
  }

  AVStream* stream = m_ctx->streams[m_video_index];
  if (!stream)
  {
    return false;
  }

  const AVCodec* codec = avcodec_find_decoder(stream->codecpar->codec_id);
  if (!codec)
  {
    std::cerr << "Decoder not found for video stream\n";
    return false;
  }

  std::unique_ptr<AVCodecContext, avcodec_context_deleter> codec_ctx(avcodec_alloc_context3(codec));
  if (!codec_ctx)
  {
    return false;
  }

  if (avcodec_parameters_to_context(codec_ctx.get(), stream->codecpar) < 0)
  {
    return false;
  }

  if (avcodec_open2(codec_ctx.get(), codec, nullptr) < 0)
  {
    return false;
  }

  const int64_t target_ts = av_rescale_q(position.count(), AVRational{ 1, 1000 }, stream->time_base);
  if (av_seek_frame(m_ctx.get(), m_video_index, target_ts, AVSEEK_FLAG_BACKWARD) < 0)
  {
    std::cerr << "seek failed\n";
    return false;
  }

  avformat_flush(m_ctx.get());
  avcodec_flush_buffers(codec_ctx.get());

  const int64_t target_pts_us = av_rescale_q(target_ts, stream->time_base, AVRational{ 1, 1000000 });

  avpacket_ptr pkt(av_packet_alloc(), avpacket_deleter{});
  avframe_ptr frame(av_frame_alloc(), avframe_deleter{});

  if (!pkt || !frame)
  {
    return false;
  }

  bool seen_video_keyframe = false;
  while (av_read_frame(m_ctx.get(), pkt.get()) >= 0)
  {
    if (pkt->stream_index != m_video_index)
    {
      av_packet_unref(pkt.get());
      continue;
    }

    if (!seen_video_keyframe)
    {
      if (!(pkt->flags & AV_PKT_FLAG_KEY))
      {
        av_packet_unref(pkt.get());
        continue;
      }

      seen_video_keyframe = true;
    }

    if (avcodec_send_packet(codec_ctx.get(), pkt.get()) < 0)
    {
      av_packet_unref(pkt.get());
      continue;
    }

    while (avcodec_receive_frame(codec_ctx.get(), frame.get()) == 0)
    {
      const int64_t frame_pts = frame->best_effort_timestamp;
      if (frame_pts == AV_NOPTS_VALUE)
      {
        av_frame_unref(frame.get());
        continue;
      }

      const int64_t frame_pts_us = av_rescale_q(frame_pts, stream->time_base, AVRational{ 1, 1000000 });
      if (frame_pts_us < target_pts_us)
      {
        av_frame_unref(frame.get());
        continue;
      }

      AVFrame* cloned = av_frame_clone(frame.get());
      if (!cloned)
      {
        return false;
      }

      m_snapshot_frame.reset(cloned, avframe_deleter{});
      av_frame_unref(frame.get());
      av_packet_unref(pkt.get());

      if (!init_encoder())
      {
        return false;
      }

      AVFrame* encode_frame = av_frame_clone(m_snapshot_frame.get());
      if (!encode_frame)
      {
        return false;
      }

      encode_frame->pts = 0;
      encode_frame->pict_type = AV_PICTURE_TYPE_I;
      encode_frame->flags |= AV_FRAME_FLAG_KEY;

      if (avcodec_send_frame(m_encoder_ctx, encode_frame) < 0)
      {
        av_frame_free(&encode_frame);
        return false;
      }

      av_frame_free(&encode_frame);

      avpacket_ptr encoded(av_packet_alloc(), avpacket_deleter{});
      if (!encoded)
      {
        return false;
      }

      while (avcodec_receive_packet(m_encoder_ctx, encoded.get()) == 0)
      {
        auto media = make_video_snapshot_packet(encoded.get(), target_ts);
        if (media)
        {
          m_snapshot_packets.push_back(std::move(media));
        }
        av_packet_unref(encoded.get());
      }

      return !m_snapshot_packets.empty();
    }

    av_packet_unref(pkt.get());
  }

  return false;
}

void snapshot_reader::build_placeholder_packets()
{
  for (const auto stream_index : m_other_stream_indexes)
  {
    auto placeholder = make_placeholder_packet(stream_index);
    if (placeholder)
    {
      m_snapshot_packets.push_back(std::move(placeholder));
    }
  }
}

bool snapshot_reader::init_encoder()
{
  const AVCodec* encoder = avcodec_find_encoder(AV_CODEC_ID_H264);
  if (!encoder || !m_snapshot_frame)
  {
    return false;
  }

  m_encoder_ctx = avcodec_alloc_context3(encoder);
  if (!m_encoder_ctx)
  {
    return false;
  }

  m_encoder_ctx->width = m_snapshot_frame->width;
  m_encoder_ctx->height = m_snapshot_frame->height;
  m_encoder_ctx->pix_fmt = static_cast<AVPixelFormat>(m_snapshot_frame->format);
  m_encoder_ctx->time_base = AVRational{ 1, 25 };
  m_encoder_ctx->framerate = AVRational{ 25, 1 };
  m_encoder_ctx->gop_size = 1;
  m_encoder_ctx->max_b_frames = 0;
  m_encoder_ctx->refs = 1;
  m_encoder_ctx->keyint_min = 1;
  m_encoder_ctx->strict_std_compliance = FF_COMPLIANCE_STRICT;

  av_opt_set(m_encoder_ctx->priv_data, "preset", "veryfast", 0);
  av_opt_set(m_encoder_ctx->priv_data, "tune", "zerolatency", 0);

  if (avcodec_open2(m_encoder_ctx, encoder, nullptr) < 0)
  {
    return false;
  }

  return true;
}

std::shared_ptr<media_packet> snapshot_reader::make_video_snapshot_packet(AVPacket* pkt, int64_t target_ts) const
{
  if (!pkt || !m_encoder_ctx || !m_ctx || m_video_index < 0)
  {
    return nullptr;
  }

  AVStream* source_stream = m_ctx->streams[m_video_index];
  if (!source_stream)
  {
    return nullptr;
  }

  avpacket_ptr packet_copy(av_packet_alloc(), avpacket_deleter{});
  if (!packet_copy)
  {
    return nullptr;
  }

  if (av_packet_ref(packet_copy.get(), pkt) < 0)
  {
    return nullptr;
  }

  packet_copy->stream_index = m_video_index;
  packet_copy->pts = target_ts;
  packet_copy->dts = target_ts;
  packet_copy->duration = 0;

  auto media = std::make_shared<media_packet>();
  media->packet = std::move(packet_copy);
  media->stream_index = m_video_index;
  media->time_base = source_stream->time_base;
  media->media_type = AVMEDIA_TYPE_VIDEO;
  media->frame_rate = source_stream->avg_frame_rate;
  media->sample_rate = source_stream->codecpar->sample_rate;

  avcodec_parameters_ptr codec_copy(avcodec_parameters_alloc(), [](AVCodecParameters* p) { avcodec_parameters_free(&p); });
  if (!codec_copy || avcodec_parameters_from_context(codec_copy.get(), m_encoder_ctx) < 0)
  {
    return nullptr;
  }

  media->codec_parameters = std::move(codec_copy);
  return media;
}

std::shared_ptr<media_packet> snapshot_reader::make_placeholder_packet(int stream_index) const
{
  if (!m_ctx || stream_index < 0 || static_cast<unsigned>(stream_index) >= m_ctx->nb_streams)
  {
    return nullptr;
  }

  AVStream* stream = m_ctx->streams[stream_index];
  if (!stream)
  {
    return nullptr;
  }

  avpacket_ptr packet(av_packet_alloc(), avpacket_deleter{});
  if (!packet)
  {
    return nullptr;
  }

  packet->pts = 0;
  packet->dts = 0;
  packet->duration = 0;
  packet->stream_index = stream_index;

  avcodec_parameters_ptr codec_copy(avcodec_parameters_alloc(), [](AVCodecParameters* p) { avcodec_parameters_free(&p); });
  if (!codec_copy || avcodec_parameters_copy(codec_copy.get(), stream->codecpar) < 0)
  {
    return nullptr;
  }

  auto media = std::make_shared<media_packet>();
  media->packet = std::move(packet);
  media->stream_index = stream_index;
  media->time_base = stream->time_base;
  media->media_type = stream->codecpar->codec_type;
  media->frame_rate = stream->avg_frame_rate;
  media->sample_rate = stream->codecpar->sample_rate;
  media->codec_parameters = std::move(codec_copy);
  return media;
}
