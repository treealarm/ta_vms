#include <algorithm>
#include <chrono>
#include <ctime>
#include <filesystem>
#include <iomanip>
#include <iostream>
#include <optional>
#include <sstream>
#include <utility>

#include "file_reader.h"
#include "interfaces/av_deleters.h"

namespace
{
  static int64_t to_us(int64_t pts, AVRational time_base)
  {
    if (pts == AV_NOPTS_VALUE)
      return -1;

    return av_rescale_q(pts, time_base, AVRational{ 1, 1000000 });
  }
}
file_reader::file_reader()
{

}

file_reader::file_reader(std::string camera_id, std::string storage_base_path)
    : m_camera_id(std::move(camera_id)),
      m_storage_base_path(std::move(storage_base_path)) {}

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

bool file_reader::connect_current_file(const std::string& file_path)
{
  disconnect();


  if (avformat_open_input(&m_ctx, file_path.c_str(), nullptr, nullptr) < 0) 
  {
    std::cerr << "Failed to open file source: " << file_path << "\n";
    return false;
  }

  if (avformat_find_stream_info(m_ctx, nullptr) < 0)
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
      m_video_index = static_cast<int>(i);
    }

    if (media_type == AVMEDIA_TYPE_VIDEO || media_type == AVMEDIA_TYPE_AUDIO)
    {
      m_forward_stream_indexes.insert(static_cast<int>(i));
    }
  }

  if (m_video_index < 0) 
  {
    std::cerr << "No video stream\n";
    disconnect();
    return false;
  }

  std::cerr << "File source opened: " << file_path << "\n";
  return true;
}

void file_reader::disconnect() 
{
  if (m_ctx) 
  {
    avformat_close_input(&m_ctx);
  }
}

void file_reader::add_sink(std::shared_ptr<media_sink> sink) {
  m_sinks_helper.add_sink(sink);
}

void file_reader::remove_sink(std::shared_ptr<media_sink> sink) {
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

void file_reader::read_loop()
{
  using clock = std::chrono::steady_clock;

  int64_t first_pts_us = -1;
  clock::time_point start_wall_time;

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
    int ret = av_read_frame(m_ctx, pkt.get());

    if (ret < 0)
    {
      std::this_thread::sleep_for(std::chrono::milliseconds(5));
      continue;
    }

    if (!m_forward_stream_indexes.contains(pkt->stream_index))
      continue;

    AVStream* stream = m_ctx->streams[pkt->stream_index];

    int64_t pts_us = to_us(pkt->pts, stream->time_base);
    if (pts_us < 0)
      continue;

    // --- инициализация базового времени ---
    if (first_pts_us < 0)
    {
      first_pts_us = pts_us;
      start_wall_time = clock::now();
    }

    int64_t relative_us = pts_us - first_pts_us;

    auto target_time = start_wall_time +
      std::chrono::microseconds(relative_us);

    constexpr auto MAX_SLEEP = std::chrono::seconds(1);
    constexpr auto MAX_LAG = std::chrono::milliseconds(500);

    // --- ожидание ---
    auto now = clock::now();

    if (target_time > now)
    {
      auto sleep_duration = target_time - now;

      if (sleep_duration > MAX_SLEEP)
      {
        // слишком большой разрыв → пересинхронизация
        start_wall_time = now - std::chrono::microseconds(relative_us);
        // после этого target_time ≈ now
      }
      else
      {
        std::this_thread::sleep_for(sleep_duration);
      }
    }
    else
    {
      auto lag = now - target_time;

      if (lag > MAX_LAG)
      {
        // сильно отстали → пересинхронизация
        start_wall_time = now - std::chrono::microseconds(relative_us);
      }
      // иначе просто отправляем без sleep
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
