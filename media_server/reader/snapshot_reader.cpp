#include "snapshot_reader.h"

#include <iostream>

#include "interfaces/av_deleters.h"

snapshot_reader::snapshot_reader(
  std::chrono::milliseconds position,
  std::function<void(const reader_progress&)> cb_progress)
  : m_position(position), m_on_progress(std::move(cb_progress))
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

void snapshot_reader::add_sink(std::shared_ptr<media_sink> sink)
{
  m_sinks_helper.add_sink(sink);
}

void snapshot_reader::remove_sink(std::shared_ptr<media_sink> sink)
{
  m_sinks_helper.remove_sink(sink);
}

AVStream* snapshot_reader::video_stream() const
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
  m_snapshot_packet.reset();
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

    bool reached = false;
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

      av_frame_unref(frame.get());

      if (frame_pts_us >= target_pts_us)
      {
        reached = true;
        break;
      }
    }

    if (reached)
    {
      avpacket_ptr cloned(av_packet_alloc(), avpacket_deleter{});
      if (!cloned)
      {
        av_packet_unref(pkt.get());
        return false;
      }

      if (av_packet_ref(cloned.get(), pkt.get()) < 0)
      {
        av_packet_unref(pkt.get());
        return false;
      }

      m_snapshot_packet = std::move(cloned);
      av_packet_unref(pkt.get());
      notify_progress();
      return true;
    }

    av_packet_unref(pkt.get());
  }

  return false;
}

void snapshot_reader::notify_progress() const
{
  if (!m_on_progress)
  {
    return;
  }

  reader_progress progress;
  progress.duration = std::chrono::milliseconds(m_ctx && m_ctx->duration > 0 ? m_ctx->duration / 1000 : 0);
  progress.position = m_position;
  if (progress.position > progress.duration && progress.duration.count() > 0)
  {
    progress.position = progress.duration;
  }

  m_on_progress(progress);
}

void snapshot_reader::read_loop()
{
  if (!m_ctx || m_video_index < 0 || !m_snapshot_packet)
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

  const int64_t pts_step = std::max<int64_t>(1, av_rescale_q(1, AVRational{ 1, 1 }, stream->time_base));
  int64_t seq = 0;

  while (m_running)
  {
    if (m_paused)
    {
      std::this_thread::sleep_for(std::chrono::milliseconds(10));
      continue;
    }

    auto media = std::make_shared<media_packet>();
    media->packet = std::shared_ptr<AVPacket>(av_packet_alloc(), avpacket_deleter{});
    if (!media->packet)
    {
      std::this_thread::sleep_for(std::chrono::milliseconds(100));
      continue;
    }

    if (av_packet_ref(media->packet.get(), m_snapshot_packet.get()) < 0)
    {
      std::this_thread::sleep_for(std::chrono::milliseconds(100));
      continue;
    }

    const int64_t base_pts = m_snapshot_packet->pts == AV_NOPTS_VALUE ? 0 : m_snapshot_packet->pts;
    const int64_t base_dts = m_snapshot_packet->dts == AV_NOPTS_VALUE ? base_pts : m_snapshot_packet->dts;

    media->packet->pts = base_pts + seq * pts_step;
    media->packet->dts = base_dts + seq * pts_step;
    media->packet->duration = pts_step;

    media->stream_index = m_video_index;
    media->time_base = stream->time_base;
    media->media_type = AVMEDIA_TYPE_VIDEO;
    media->frame_rate = stream->avg_frame_rate;
    media->sample_rate = 0;
    media->nb_samples = 0;

    avcodec_parameters_ptr codec_copy(
      avcodec_parameters_alloc(),
      [](AVCodecParameters* p) { avcodec_parameters_free(&p); });

    if (codec_copy && avcodec_parameters_copy(codec_copy.get(), stream->codecpar) >= 0)
    {
      media->codec_parameters = std::move(codec_copy);

      for (auto& sink : m_sinks_helper.snapshot_sinks())
      {
        sink->on_packet(media);
      }

      ++seq;
    }

    std::this_thread::sleep_for(std::chrono::seconds(1));
  }

  disconnect();
}
