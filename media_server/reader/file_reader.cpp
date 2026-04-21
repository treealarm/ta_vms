#include <ctime>
#include <format>
#include <utility>

#include "file_reader.h"
#include "interfaces/av_deleters.h"
#include "logger/logger.h"

file_reader::file_reader(
  std::string camera_id,
  std::chrono::milliseconds position,
  std::function<bool(const std::string&)> cb_eof,
  std::function<void(const reader_progress&)> cb_progress
)
    :
  m_packet_transcoder({
  .video_codec = AV_CODEC_ID_H264,
  .audio_codec = AV_CODEC_ID_OPUS,
  .allow_hevc_video_passthrough = false,
  .allow_all_audio_passthrough = false
}),
  m_camera_id(std::move(camera_id)),
  m_start_position_ms(position),
  m_on_file_end(cb_eof),
  m_on_progress(cb_progress),
  m_cur_video_pos(position)
{}

file_reader::~file_reader() 
{
  stop();
  m_packet_transcoder.close();
  disconnect();
}

bool file_reader::open(const std::string& source) 
{
  disconnect();
  std::lock_guard lock(m_mutex);
  m_source = source;
  return true;
}

std::string file_reader::get_source()
{
  std::lock_guard lock(m_mutex);
  return m_source;
}

void file_reader::disconnect() 
{
  m_ctx.reset();
}

void file_reader::add_sink(std::shared_ptr<media_sink> sink) 
{
  m_sinks_helper.add_sink(sink);
}

void file_reader::remove_sink(std::shared_ptr<media_sink> sink)
{
  m_sinks_helper.remove_sink(sink);
}

AVStream* file_reader::video_stream() const 
{
  if (m_ctx && m_video_index >= 0) 
  {
    return m_ctx->streams[m_video_index];
  }

  return nullptr;
}

void file_reader::start() 
{
  if (m_running) 
  {
    return;
  }

  m_running = true;
  m_thread = std::thread([this] 
    { 
      read_loop(); 
    }
  );
}

void file_reader::stop() 
{
  m_running = false;
  if (m_thread.joinable()) 
  {
    m_thread.join();
  }
}

void file_reader::pause()
{
  m_paused = true;
  m_last_pause_emit_at = clock::time_point::min();
}

void file_reader::resume()
{
  m_paused = false;
  m_last_pause_emit_at = clock::time_point::min();
}

void file_reader::step(int32_t steps)
{
  m_steps = steps;
}

void file_reader::set_speed(double speed)
{
  m_play_speed = speed;
  if (m_play_speed < 0.1)
    m_play_speed = 0.1;
}

bool file_reader::connect_current_file()
{
  disconnect();
  m_forward_stream_indexes.clear();
  m_video_index = -1;
  m_master_stream_index = -1;

  AVFormatContext* ctx = nullptr;

  if (avformat_open_input(&ctx, get_source().c_str(), nullptr, nullptr) < 0)
  {
    logger::instance().error(std::format("Failed to open file source: {}", get_source()));
    return false;
  }

  m_ctx.reset(ctx);

  if (avformat_find_stream_info(m_ctx.get(), nullptr) < 0)
  {
    logger::instance().error("Failed to get stream info");
    disconnect();
    return false;
  }

  for (unsigned i = 0; i < m_ctx->nb_streams; ++i)
  {
    const AVMediaType media_type = m_ctx->streams[i]->codecpar->codec_type;
    if (media_type == AVMEDIA_TYPE_VIDEO && m_video_index < 0)
    {
      m_video_index = static_cast<int32_t>(i);
    }
    if (media_type == AVMEDIA_TYPE_AUDIO)
    {
      m_master_stream_index = static_cast<int32_t>(i);
    }
    if (media_type == AVMEDIA_TYPE_VIDEO || media_type == AVMEDIA_TYPE_AUDIO)
    {
      m_forward_stream_indexes.insert(static_cast<int32_t>(i));
    }
  }

  if (m_master_stream_index < 0)
    m_master_stream_index = m_video_index;

  if (m_video_index < 0)
  {
    logger::instance().error("No video stream");
    disconnect();
    return false;
  }

  if (m_start_position_ms.count() > 0)
  {
    if (!seek_to_position(m_start_position_ms))
    {
      logger::instance().error("seek_to_position failed");
    }
  }

  logger::instance().info(std::format("File source opened: {}", get_source()));
  return true;
}


std::chrono::milliseconds file_reader::sync_to_master_clock(int64_t pkt_pts)
{
  using clock = std::chrono::steady_clock;
  auto now = clock::now();

  if (m_master_pts < 0)
  {
    m_master_pts = pkt_pts;
    m_master_wall_time = now;
    return std::chrono::milliseconds(0);
  }

  int64_t delta = pkt_pts - m_master_pts;
  if (delta < 0)
    delta = 0;

  // учет скорости воспроизведения
  double speed = (m_play_speed <= 0.0) ? 1.0 : m_play_speed.load();
  int64_t scaled_delta = static_cast<int64_t>(delta / speed);

  auto target_time = m_master_wall_time + std::chrono::milliseconds(scaled_delta);

  constexpr auto MAX_SLEEP = std::chrono::seconds(1);
  constexpr auto MAX_LAG = std::chrono::milliseconds(500);

  if (target_time > now)
  {
    auto sleep_duration = target_time - now;

    if (sleep_duration > MAX_SLEEP)
    {
      m_master_wall_time = now - std::chrono::milliseconds(scaled_delta);
      return std::chrono::milliseconds(0);
    }

    return std::chrono::duration_cast<std::chrono::milliseconds>(sleep_duration);
  }
  else
  {
    auto lag = now - target_time;
    if (lag > MAX_LAG)
    {
      m_master_wall_time = now - std::chrono::milliseconds(scaled_delta);
    }

    return std::chrono::milliseconds(0);
  }
}

void file_reader::read_loop()
{
  while (m_running)
  {
    if (m_paused)
    {
      emit_paused_snapshots();
      std::this_thread::sleep_for(std::chrono::milliseconds(10));
      continue;
    }
    else
    {
      m_snapshot_reader.reset();
    }

    if (!m_ctx || m_video_index < 0 || m_forward_stream_indexes.empty())
    {
      if (!connect_current_file())
        break;

      std::this_thread::sleep_for(std::chrono::milliseconds(5));
      continue;
    }

    avpacket_ptr pkt(av_packet_alloc(), avpacket_deleter{});
    int ret = av_read_frame(m_ctx.get(), pkt.get());

    if (ret < 0)
    {
      if (AVERROR_EOF == ret)
      {
        if (m_on_file_end)
        {
          auto succ = m_on_file_end(get_source());
          if (!succ)
          {
            // не стоит закрывать ридер, пока он пересоздается на проигрывание.  
            std::this_thread::sleep_for(std::chrono::milliseconds(100));         
          }
        }
      }
      std::this_thread::sleep_for(std::chrono::milliseconds(5));
      continue;
    }

    if (!m_forward_stream_indexes.contains(pkt->stream_index))
      continue;

    AVStream* stream = m_ctx->streams[pkt->stream_index];

    if (pkt->stream_index == m_video_index)
    {
      if (m_drop_until_keyframe)
      {
        if (!(pkt->flags & AV_PKT_FLAG_KEY))
        {
          continue;
        }

        m_drop_until_keyframe = false;
      }

      m_cur_video_pos = std::chrono::milliseconds(
        av_rescale_q(
        pkt->pts,
        stream->time_base,
        AVRational{ 1, 1000 }
      ));
    }

    if (pkt->stream_index == m_master_stream_index)
    {
      int64_t pts_ms = av_rescale_q(pkt->pts, stream->time_base, AVRational{ 1, 1000 });

      auto sleep_ms = sync_to_master_clock(pts_ms);
      if (sleep_ms.count() > 0)
      {
        std::this_thread::sleep_for(sleep_ms);
      } 
      on_progress(std::chrono::milliseconds(pts_ms), get_duration());
    } 

    double speed = (m_play_speed <= 0.0) ? 1.0 : m_play_speed.load();

    if (speed > 1)
    {
      pkt->pts = static_cast<int64_t>(pkt->pts / speed);
      pkt->dts = static_cast<int64_t>(pkt->dts / speed);
      if (pkt->duration > 0)
        pkt->duration = static_cast<int64_t>(pkt->duration / speed);
    }    

    // --- формирование пакета ---
    auto media = std::make_shared<media_packet>();
    media->packet = pkt;
    media->stream_index = pkt->stream_index;
    media->time_base = stream->time_base;
    media->media_type = stream->codecpar->codec_type;
    media->frame_rate = stream->avg_frame_rate;
    media->sample_rate = stream->codecpar->sample_rate;
    media->nb_samples = 0;

    avcodec_parameters_ptr codec_copy(
      avcodec_parameters_alloc(),
      [](AVCodecParameters* p) { avcodec_parameters_free(&p); });

    if (!codec_copy)
      continue;

    if (avcodec_parameters_copy(codec_copy.get(), stream->codecpar) < 0)
      continue;

    media->codec_parameters = std::move(codec_copy);

    // --- отправка ---

    auto out_media = m_packet_transcoder.transcode_packet(media);

    while (out_media)
    {
      for (auto& sink : m_sinks_helper.snapshot_sinks())
      {
        sink->on_packet(out_media);
      }

      out_media = m_packet_transcoder.transcode_packet(nullptr);
    }
  }

  m_packet_transcoder.close();
  disconnect();
}

void file_reader::on_progress(std::chrono::milliseconds pts_ms, std::chrono::milliseconds duration)
{
  if (m_on_progress)
  {
    reader_progress progress;

    progress.duration = duration;
    progress.position = pts_ms;

    if (progress.position > progress.duration)
      progress.position = progress.duration;

    m_on_progress(progress);
  }
}

void file_reader::emit_paused_snapshots()
{
  const auto now = clock::now();
  if (m_last_pause_emit_at != clock::time_point::min() &&
      now - m_last_pause_emit_at < std::chrono::seconds(1))
  {
    return;
  }

  if (!m_snapshot_reader)
  {
    m_snapshot_reader = std::make_unique<snapshot_reader>(m_cur_video_pos);
    if (!m_snapshot_reader->open(get_source()))
    {
      logger::instance().error(std::format("snapshot_reader open failed for source: {}", get_source()));
    }
  }

  if (m_steps != 0)
  {
    if (m_steps > 0)
    {
      m_snapshot_reader->forward();
    }
    else
    {
      m_snapshot_reader->prev();
    } 
    m_steps = 0;
  }
  const auto snapshots = m_snapshot_reader->snapshot_packets();

  for (const auto& media : snapshots)
  {
    auto out_media = m_packet_transcoder.transcode_packet(media);
    while (out_media)
    {
      for (auto& sink : m_sinks_helper.snapshot_sinks())
      {
        sink->on_packet(out_media);
      }

      out_media = m_packet_transcoder.transcode_packet(nullptr);
    }
  }

  on_progress(m_snapshot_reader->current_position(), m_snapshot_reader->get_duration());

  m_last_pause_emit_at = now;
}

std::chrono::milliseconds file_reader::get_duration()
{
  std::chrono::milliseconds duration_ms;
  if (m_ctx && m_ctx->duration > 0)
  {
    duration_ms = std::chrono::milliseconds(m_ctx->duration / 1000);
  }
  return duration_ms;
}

bool file_reader::seek_to_position(std::chrono::milliseconds position)
{
  if (!m_ctx || m_video_index < 0)
    return false;


  auto duration = get_duration();
  if (position > duration)
  {
    position = duration - std::chrono::milliseconds(1000);
  }

  AVStream* stream = m_ctx->streams[m_video_index];
  if (!stream)
    return false;

  int64_t target_ts = av_rescale_q(
    position.count(),
    AVRational{ 1, 1000 },
    stream->time_base
  );

  if (av_seek_frame(m_ctx.get(), m_video_index, target_ts, AVSEEK_FLAG_BACKWARD) < 0)
  {
    logger::instance().error("seek failed");
    return false;
  }

  avformat_flush(m_ctx.get());

  // reset state
  m_master_pts = -1;
  m_packet_transcoder.close();

  // IMPORTANT: начать только с keyframe
  m_drop_until_keyframe = true;

  return true;
}
