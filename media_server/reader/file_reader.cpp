#include <algorithm>
#include <ctime>
#include <filesystem>
#include <iomanip>
#include <iostream>
#include <optional>
#include <sstream>
#include <utility>

#include "file_reader.h"
#include "interfaces/av_deleters.h"

file_reader::file_reader(
  std::string camera_id,
  std::function<bool(const std::string&)> cb_eof,
  std::function<void(const reader_progress&)> cb_progress
)
    : 
  m_camera_id(std::move(camera_id)),
  m_on_file_end(cb_eof),
  m_on_progress(cb_progress)
{}

file_reader::~file_reader() 
{
  stop();
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

bool file_reader::connect_current_file(const std::string& file_path)
{
  disconnect();

  AVFormatContext* ctx = nullptr;

  if (avformat_open_input(&ctx, file_path.c_str(), nullptr, nullptr) < 0)
  {
    std::cerr << "Failed to open file source: " << file_path << "\n";
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
    std::cerr << "No video stream\n";
    disconnect();
    return false;
  }

  std::cerr << "File source opened: " << file_path << "\n";
  return true;
}


std::chrono::microseconds file_reader::sync_to_master_clock(int64_t pkt_pts_us)
{
  using clock = std::chrono::steady_clock;
  auto now = clock::now();

  // инициализация master
  if (m_master_pts_us < 0)
  {
    m_master_pts_us = pkt_pts_us;
    m_master_wall_time = now;
    return std::chrono::microseconds(0); // первый пакет — без ожидания
  }

  int64_t delta_us = pkt_pts_us - m_master_pts_us;
  if (delta_us < 0)
    delta_us = 0;

  auto target_time = m_master_wall_time + std::chrono::microseconds(delta_us);

  constexpr auto MAX_SLEEP = std::chrono::seconds(1);
  constexpr auto MAX_LAG = std::chrono::milliseconds(500);

  if (target_time > now)
  {
    auto sleep_duration = target_time - now;
    if (sleep_duration > MAX_SLEEP)
    {
      // пересинхронизация
      m_master_wall_time = now - std::chrono::microseconds(delta_us);
      return std::chrono::microseconds(0);
    }
    return std::chrono::duration_cast<std::chrono::microseconds>(sleep_duration);
  }
  else
  {
    auto lag = now - target_time;
    if (lag > MAX_LAG)
    {
      // сильно отстали → пересинхронизация
      m_master_wall_time = now - std::chrono::microseconds(delta_us);
    }
    return std::chrono::microseconds(0); // уже поздно, отправляем сразу
  }
}

void file_reader::read_loop()
{
  while (m_running)
  {
    if (!m_ctx || m_video_index < 0 || m_forward_stream_indexes.empty())
    {
      if (!connect_current_file(get_source()))
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
            // возможно не стоит закрывать ридер, пока он пересоздается на проигрывание.
            m_running = false;
            break;
          }
        }
      }
      std::this_thread::sleep_for(std::chrono::milliseconds(5));
      continue;
    }

    if (!m_forward_stream_indexes.contains(pkt->stream_index))
      continue;

    AVStream* stream = m_ctx->streams[pkt->stream_index];

    if (pkt->stream_index == m_master_stream_index)
    {
      int64_t pts_us = av_rescale_q(pkt->pts, stream->time_base, AVRational{ 1, 1000000 });

      auto sleep_us = sync_to_master_clock(pts_us);
      if (sleep_us.count() > 0)
        std::this_thread::sleep_for(sleep_us);
    }
    
    if (pkt->stream_index == m_master_stream_index && m_on_progress)
    {
      using namespace std::chrono;

      int64_t duration_ms = 0;
      if (m_ctx && m_ctx->duration > 0)
        duration_ms = m_ctx->duration / 1000; // из микросекунд в миллисекунды

      // Текущая позиция относительно master
      int64_t position_ms = 0;
      if (m_master_pts_us >= 0)
      {
        auto elapsed_us = m_master_pts_us + duration_cast<microseconds>(steady_clock::now() - m_master_wall_time).count();
        position_ms = elapsed_us / 1000; // в миллисекундах
      }

      if (position_ms > duration_ms)
        position_ms = duration_ms;

      reader_progress progress;
      progress.duration = milliseconds(duration_ms);
      progress.position = milliseconds(position_ms);

      // Можно вызывать асинхронно в отдельном потоке UI, если нужно
      m_on_progress(progress);
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
    for (auto& sink : m_sinks_helper.snapshot_sinks())
    {
      auto clone = clone_media_packet(media);
      if (!clone)
        continue;

      sink->on_packet(clone);
    }
  }

  disconnect();
}

