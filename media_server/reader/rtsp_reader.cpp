#include <algorithm>
#include <chrono>
#include <iostream>
#include <thread>
#include "media/av_deleters.h"
#include "rtsp_reader.h"

namespace {
constexpr auto kWatchdogInterval = std::chrono::seconds(5);
}

rtsp_reader::rtsp_reader() = default;
rtsp_reader::~rtsp_reader() {
  stop();
  disconnect();
}

bool rtsp_reader::open(const std::string& url) {
  m_rtsp_url = url;
  disconnect();
  return connect();
}

bool rtsp_reader::connect() {
  disconnect();

  AVDictionary* opts = nullptr;
  av_dict_set(&opts, "rtsp_transport", "tcp", 0);
  av_dict_set(&opts, "stimeout", "5000000", 0);

  if (avformat_open_input(&m_ctx, m_rtsp_url.c_str(), nullptr, &opts) < 0) {
    std::cerr << "Failed to open RTSP\n";
    av_dict_free(&opts);
    return false;
  }

  av_dict_free(&opts);

  if (avformat_find_stream_info(m_ctx, nullptr) < 0) {
    std::cerr << "Failed to get stream info\n";
    disconnect();
    return false;
  }

  m_video_index = -1;
  for (unsigned i = 0; i < m_ctx->nb_streams; ++i) {
    if (m_ctx->streams[i]->codecpar->codec_type == AVMEDIA_TYPE_VIDEO) {
      m_video_index = static_cast<int>(i);
      break;
    }
  }

  if (m_video_index < 0) {
    std::cerr << "No video stream\n";
    disconnect();
    return false;
  }

  std::cerr << "RTSP connected\n";
  return true;
}

void rtsp_reader::disconnect() {
  m_video_index = -1;
  if (m_ctx)
    avformat_close_input(&m_ctx);
}

void rtsp_reader::add_sink(media_sink* sink) {
  std::scoped_lock lock(m_sinks_mutex);
  m_sinks.push_back(sink);
}

void rtsp_reader::remove_sink(media_sink* sink) {
  std::scoped_lock lock(m_sinks_mutex);
  m_sinks.erase(std::remove(m_sinks.begin(), m_sinks.end(), sink), m_sinks.end());
}

AVStream* rtsp_reader::video_stream() const {
  if (m_ctx && m_video_index >= 0)
    return m_ctx->streams[m_video_index];
  return nullptr;
}

void rtsp_reader::start() {
  if (m_running) return;
  m_running = true;
  m_thread = std::thread([this] { read_loop(); });
}

void rtsp_reader::stop() {
  m_running = false;
  if (m_thread.joinable())
    m_thread.join();
}

void rtsp_reader::read_loop() {
  auto last_frame_at = std::chrono::steady_clock::now();

  while (m_running) {
    if (!m_ctx || m_video_index < 0) {
      if (!connect()) {
        std::this_thread::sleep_for(kWatchdogInterval);
        continue;
      }
      last_frame_at = std::chrono::steady_clock::now();
    }

    avpacket_ptr pkt(av_packet_alloc(), avpacket_deleter{});
    const int read_result = av_read_frame(m_ctx, pkt.get());

    if (read_result < 0) {
      const auto now = std::chrono::steady_clock::now();
      if (now - last_frame_at >= kWatchdogInterval) {
        std::cerr << "RTSP reader watchdog: reconnecting due to read error\n";
        disconnect();
      }
      continue;
    }

    if (pkt->stream_index != m_video_index)
      continue;

    last_frame_at = std::chrono::steady_clock::now();

    auto media = std::make_shared<media_packet>();
    media->packet = pkt;
    media->stream_index = m_video_index;
    media->time_base = m_ctx->streams[m_video_index]->time_base;

    std::vector<media_sink*> sinks_copy;
    {
      std::scoped_lock lock(m_sinks_mutex);
      sinks_copy = m_sinks;
    }

    for (auto* sink : sinks_copy) {
      auto sink_media = clone_media_packet(media);
      if (!sink_media)
        continue;
      sink->on_packet(sink_media);
    }
  }

  disconnect();
}
