#include "snapshot_reader.h"

#include <iostream>

#include "interfaces/av_deleters.h"

snapshot_reader::snapshot_reader(
  std::chrono::milliseconds position)
  : m_position(position)
{}

snapshot_reader::~snapshot_reader()
{
  stop();
  disconnect();
}

bool snapshot_reader::open(const std::string& source)
{
  disconnect();
  std::lock_guard lock(m_mutex);
  m_source = source;
  return true;
}


AVStream* snapshot_reader::video_stream()
{
  if (m_ctx && m_video_index >= 0)
  {
    return m_ctx->streams[m_video_index];
  }

  return nullptr;
}

void snapshot_reader::start()
{
  if (m_running)
  {
    return;
  }

  m_running = true;
  m_thread = std::thread([this]
    {
      read_loop();
    });
}

void snapshot_reader::stop()
{
  m_running = false;
  if (m_thread.joinable())
  {
    m_thread.join();
  }
}

std::string snapshot_reader::get_source()
{
  std::lock_guard lock(m_mutex);
  return m_source;
}

void snapshot_reader::disconnect()
{
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
    if (m_ctx->streams[i]->codecpar->codec_type == AVMEDIA_TYPE_VIDEO)
    {
      m_video_index = static_cast<int>(i);
      break;
    }
  }

  if (m_video_index < 0)
  {
    std::cerr << "No video stream\n";
    disconnect();
    return false;
  }

  return decode_to_snapshot(m_position);
}

bool snapshot_reader::decode_to_snapshot(std::chrono::milliseconds position)
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

  std::unique_ptr<AVCodecContext, avcodec_context_deleter> codec_ctx(
    avcodec_alloc_context3(codec));
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

  const int64_t target_ts = av_rescale_q(
    position.count(),
    AVRational{ 1, 1000 },
    stream->time_base);

  if (av_seek_frame(m_ctx.get(), m_video_index, target_ts, AVSEEK_FLAG_BACKWARD) < 0)
  {
    std::cerr << "seek failed\n";
    return false;
  }

  avformat_flush(m_ctx.get());
  avcodec_flush_buffers(codec_ctx.get());

  const int64_t target_pts_us = av_rescale_q(
    target_ts,
    stream->time_base,
    AVRational{ 1, 1000000 });

  avpacket_ptr pkt(av_packet_alloc(), avpacket_deleter{});
  avframe_ptr frame(av_frame_alloc(), avframe_deleter{});

  if (!pkt || !frame)
  {
    return false;
  }

  while (av_read_frame(m_ctx.get(), pkt.get()) >= 0)
  {
    if (pkt->stream_index != m_video_index)
    {
      av_packet_unref(pkt.get());
      continue;
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

      const int64_t frame_pts_us = av_rescale_q(
        frame_pts,
        stream->time_base,
        AVRational{ 1, 1000000 });

      if (frame_pts_us >= target_pts_us)
      {
        // 🔥 ВАЖНО: сохраняем FRAME
        AVFrame* cloned = av_frame_clone(frame.get());
        if (!cloned)
          return false;

        m_snapshot_frame.reset(cloned, avframe_deleter{});

        av_frame_unref(frame.get());
        av_packet_unref(pkt.get());
        return true;
      }

      av_frame_unref(frame.get());
    }

    av_packet_unref(pkt.get());
  }

  return false;
}

void snapshot_reader::read_loop()
{
  if (!m_ctx || m_video_index < 0 || !m_snapshot_frame)
  {
    if (!connect_current_file(get_source()))
    {
      m_running = false;
      return;
    }
  }

  AVStream* stream = m_ctx->streams[m_video_index];
  if (!stream)
  {
    m_running = false;
    return;
  }

  if (!m_encoder_ctx)
  {
    if (!init_encoder(stream))
    {
      m_running = false;
      return;
    }
  }

  int64_t pts = 0;

  while (m_running)
  {
    if (m_paused)
    {
      std::this_thread::sleep_for(std::chrono::milliseconds(10));
      continue;
    }

    // 🔥 копируем frame
    AVFrame* frame = av_frame_clone(m_snapshot_frame.get());
    if (!frame)
    {
      std::this_thread::sleep_for(std::chrono::milliseconds(100));
      continue;
    }

    frame->pts = pts++;
    frame->pict_type = AV_PICTURE_TYPE_I;
    frame->flags |= AV_FRAME_FLAG_KEY;
    // encode
    if (avcodec_send_frame(m_encoder_ctx, frame) < 0)
    {
      av_frame_free(&frame);
      continue;
    }

    av_frame_free(&frame);

    AVPacket* out = av_packet_alloc();
    if (!out)
      continue;

    while (avcodec_receive_packet(m_encoder_ctx, out) == 0)
    {
      auto media = std::make_shared<media_packet>();
      media->packet = avpacket_ptr(out, avpacket_deleter{});
      media->stream_index = 0;
      media->time_base = m_encoder_time_base;
      media->media_type = AVMEDIA_TYPE_VIDEO;

      avcodec_parameters_ptr codec_copy(
        avcodec_parameters_alloc(),
        [](AVCodecParameters* p) { avcodec_parameters_free(&p); });

      if (codec_copy && avcodec_parameters_from_context(codec_copy.get(), m_encoder_ctx) >= 0)
      {
        media->codec_parameters = std::move(codec_copy);

        //here media is ready to send
      }

      out = av_packet_alloc();
      if (!out)
        break;
    }

    av_packet_free(&out);

    std::this_thread::sleep_for(std::chrono::milliseconds(40)); // ~25 fps
  }

  disconnect();
}

bool snapshot_reader::init_encoder(AVStream* stream)
{
  const AVCodec* encoder = avcodec_find_encoder(AV_CODEC_ID_H264);
  if (!encoder)
    return false;

  m_encoder_ctx = avcodec_alloc_context3(encoder);
  if (!m_encoder_ctx)
    return false;

  m_encoder_ctx->width = m_snapshot_frame->width;
  m_encoder_ctx->height = m_snapshot_frame->height;
  m_encoder_ctx->pix_fmt = AV_PIX_FMT_YUV420P;
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
    return false;

  m_encoder_time_base = m_encoder_ctx->time_base;

  return true;
}