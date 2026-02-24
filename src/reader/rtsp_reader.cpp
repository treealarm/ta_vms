#include <iostream>
#include "../media/av_deleters.h"
#include "rtsp_reader.h"

rtsp_reader::rtsp_reader() = default;
rtsp_reader::~rtsp_reader() {
  stop();
  if (m_ctx)
    avformat_close_input(&m_ctx);
}

bool rtsp_reader::open(const std::string& url) {
  m_rtsp_url = url;

  AVDictionary* opts = nullptr;
  av_dict_set(&opts, "rtsp_transport", "tcp", 0);
  av_dict_set(&opts, "stimeout", "5000000", 0);

  if (avformat_open_input(&m_ctx, m_rtsp_url.c_str(), nullptr, &opts) < 0) {
    std::cerr << "Failed to open RTSP\n";
    return false;
  }

  if (avformat_find_stream_info(m_ctx, nullptr) < 0) {
    std::cerr << "Failed to get stream info\n";
    return false;
  }

  for (unsigned i = 0; i < m_ctx->nb_streams; ++i) {
    if (m_ctx->streams[i]->codecpar->codec_type == AVMEDIA_TYPE_VIDEO) {
      m_video_index = i;
      break;
    }
  }

  if (m_video_index < 0) {
    std::cerr << "No video stream\n";
    return false;
  }

  return true;
}

void rtsp_reader::add_sink(media_sink* sink) {
  m_sinks.push_back(sink);
}

AVStream* rtsp_reader::video_stream() const {
  if (m_video_index >= 0)
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
  while (m_running) {
    avpacket_ptr pkt(av_packet_alloc(), avpacket_deleter{});
    if (av_read_frame(m_ctx, pkt.get()) < 0) continue;
    if (pkt->stream_index != m_video_index) continue;

    auto media = std::make_shared<media_packet>();
    media->packet = pkt;
    media->stream_index = m_video_index;
    media->time_base = m_ctx->streams[m_video_index]->time_base;

    for (auto* sink : m_sinks)
      sink->on_packet(media);
  }
}