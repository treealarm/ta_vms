#include "snapshot_reader.h"

#include <format>

#include "interfaces/av_deleters.h"
#include "logger/logger.h"

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

  if (m_decoder_ctx)
    avcodec_free_context(&m_decoder_ctx);

  m_snapshot_packets.clear();

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
    logger::instance().error(std::format("Failed to open file source: {}", source));
    return false;
  }

  m_ctx.reset(ctx);

  if (avformat_find_stream_info(m_ctx.get(), nullptr) < 0)
  {
    logger::instance().error("Failed to get stream info");
    disconnect();
    return false;
  }

  // --- поиск стримов (оставляем как есть) ---
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
    logger::instance().error("No video stream");
    disconnect();
    return false;
  }

  // --- НОВОЕ: init decoder ---
  if (!init_decoder())
  {
    logger::instance().error("Failed to init decoder");
    disconnect();
    return false;
  }

  // --- НОВОЕ: позиция ---
  AVStream* stream = m_ctx->streams[m_video_index];

  // для seek
  int64_t safety_ms = 1000; // 0.5–2 сек обычно достаточно

  int64_t target_ts = av_rescale_q(
    m_position.count(),
    AVRational{ 1, 1000 },
    stream->time_base
  );

  int64_t seek_ts = target_ts - av_rescale_q(
    safety_ms,
    AVRational{ 1, 1000 },
    stream->time_base
  );

  seek_ts = std::max<int64_t>(seek_ts, 0);


  if (!seek_to(seek_ts))
  {
    logger::instance().error(std::format("Failed to seek file source: {}:{}", source, seek_ts));
    return false;
  }
    

  if (!decode_until(target_ts))
  {
    logger::instance().error(std::format("Failed to decode_until file source: {}:{}", source, seek_ts));
    return false;
  }    

  m_current_index = m_frame_buffer.size() - 1;

  // --- НОВОЕ: encoder init после первого кадра ---
  if (!init_encoder())
  {
    logger::instance().error("Failed to init encoder");
    disconnect();
    return false;
  }

  // --- НОВОЕ: сформировать первый snapshot ---
  if (!encode_current_frame())
  {
    logger::instance().error("Failed to encode first frame");
    disconnect();
    return false;
  }

  return true;
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
  if (m_frame_buffer.empty())
    return false;

  const AVCodec* encoder = avcodec_find_encoder(AV_CODEC_ID_H264);
  if (!encoder)
  {
    return false;
  }

  m_encoder_ctx = avcodec_alloc_context3(encoder);
  if (!m_encoder_ctx)
  {
    return false;
  }

  auto* frame = m_frame_buffer[m_current_index].frame.get();

  m_encoder_ctx->width = frame->width;
  m_encoder_ctx->height = frame->height;
  m_encoder_ctx->pix_fmt = (AVPixelFormat)frame->format;

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

bool snapshot_reader::init_decoder()
{
  AVStream* stream = m_ctx->streams[m_video_index];
  const AVCodec* codec = avcodec_find_decoder(stream->codecpar->codec_id);
  if (!codec) return false;

  m_decoder_ctx = avcodec_alloc_context3(codec);
  if (!m_decoder_ctx) return false;

  if (avcodec_parameters_to_context(m_decoder_ctx, stream->codecpar) < 0)
    return false;

  if (avcodec_open2(m_decoder_ctx, codec, nullptr) < 0)
    return false;

  return true;
}

bool snapshot_reader::decode_next_frame(std::deque<buffered_frame>& frame_buffer)
{
  avpacket_ptr pkt(av_packet_alloc(), avpacket_deleter{});
  avframe_ptr frame(av_frame_alloc(), avframe_deleter{});

  while (av_read_frame(m_ctx.get(), pkt.get()) >= 0)
  {
    if (pkt->stream_index != m_video_index)
    {
      av_packet_unref(pkt.get());
      continue;
    }

    if (avcodec_send_packet(m_decoder_ctx, pkt.get()) < 0)
    {
      av_packet_unref(pkt.get());
      continue;
    }

    while (avcodec_receive_frame(m_decoder_ctx, frame.get()) == 0)
    {
      int64_t pts = frame->best_effort_timestamp;
      if (pts == AV_NOPTS_VALUE)
      {
        av_frame_unref(frame.get());
        continue;
      }

      AVFrame* cloned = av_frame_clone(frame.get());
      if (!cloned) return false;

      buffered_frame bf;
      bf.frame.reset(cloned, avframe_deleter{});
      bf.pts = pts;

      frame_buffer.push_back(std::move(bf));

      av_frame_unref(frame.get());
      av_packet_unref(pkt.get());
      return true;
    }

    av_packet_unref(pkt.get());
  }

  return false;
}

bool snapshot_reader::encode_current_frame()
{
  if (m_frame_buffer.empty()) return false;

  const auto& bf = m_frame_buffer[m_current_index];

  AVFrame* encode_frame = av_frame_clone(bf.frame.get());
  if (!encode_frame) return false;

  encode_frame->pts = 0;
  encode_frame->pict_type = AV_PICTURE_TYPE_I;
  encode_frame->flags |= AV_FRAME_FLAG_KEY;

  if (avcodec_send_frame(m_encoder_ctx, encode_frame) < 0)
  {
    av_frame_free(&encode_frame);
    return false;
  }

  av_frame_free(&encode_frame);

  avpacket_ptr pkt(av_packet_alloc(), avpacket_deleter{});
  if (!pkt) return false;

  m_snapshot_packets.clear();

  while (avcodec_receive_packet(m_encoder_ctx, pkt.get()) == 0)
  {
    auto media = make_video_snapshot_packet(pkt.get(), bf.pts);
    if (media)
      m_snapshot_packets.push_back(std::move(media));

    av_packet_unref(pkt.get());
  }

  build_placeholder_packets();
  return !m_snapshot_packets.empty();
}

bool snapshot_reader::seek_to(int64_t target_ts)
{
  if (av_seek_frame(m_ctx.get(), m_video_index, target_ts, AVSEEK_FLAG_BACKWARD) < 0)
    return false;
  
  //if (!avformat_seek_file(
  //  m_ctx.get(),
  //  m_video_index,
  //  INT64_MIN,
  //  target_ts,
  //  INT64_MAX,
  //  AVSEEK_FLAG_BACKWARD
  //))
  //  return false;

  avformat_flush(m_ctx.get());
  avcodec_flush_buffers(m_decoder_ctx);
  return true;
}

bool snapshot_reader::decode_until(int64_t target_pts)
{
  while (true)
  {
    if (!decode_next_frame(m_frame_buffer))
      return false;

    const auto& f = m_frame_buffer.back();

    if (f.pts >= target_pts)
      return true;
  }
}

