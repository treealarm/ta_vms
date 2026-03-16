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
  
  return true;
}


bool rtsp_reader::connect() {
  disconnect();

  AVDictionary* opts = nullptr;
  av_dict_set(&opts, "rtsp_transport", "tcp", 0);
  av_dict_set(&opts, "rtsp_flags", "prefer_tcp", 0);
  av_dict_set(&opts, "timeout", "5000000", 0);
  av_dict_set(&opts, "rw_timeout", "5000000", 0);

  AVFormatContext* ctx = avformat_alloc_context();

  if (avformat_open_input(&ctx, m_rtsp_url.c_str(), nullptr, &opts) < 0) {
    std::cerr << "Failed to open RTSP\n";
    av_dict_free(&opts);
    avformat_close_input(&ctx);
    return false;
  }

  if (ctx != m_ctx.get()) {
    m_ctx.reset(ctx);
  }

  av_dict_free(&opts);

  if (avformat_find_stream_info(m_ctx.get(), nullptr) < 0) {
    std::cerr << "Failed to get stream info\n";
    disconnect();
    return false;
  }

  m_video_index = -1;
  m_forward_stream_indexes.clear();
  for (unsigned i = 0; i < m_ctx->nb_streams; ++i) {
    const AVMediaType media_type = m_ctx->streams[i]->codecpar->codec_type;
    if (media_type == AVMEDIA_TYPE_VIDEO && m_video_index < 0) {
      m_video_index = static_cast<int>(i);
    }

    if (media_type == AVMEDIA_TYPE_VIDEO || media_type == AVMEDIA_TYPE_AUDIO) {
      m_forward_stream_indexes.insert(static_cast<int>(i));
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
  m_forward_stream_indexes.clear();
  m_ctx.reset();
}

void rtsp_reader::add_sink(media_sink* sink) {
  std::scoped_lock lock(m_sink_mutex);
  m_sink = sink;
}

void rtsp_reader::remove_sink(media_sink* sink) {
  std::scoped_lock lock(m_sink_mutex);
  if (m_sink == sink) {
    m_sink = nullptr;
  }
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
    if (!m_ctx || m_video_index < 0 || m_forward_stream_indexes.empty()) {
      if (!connect()) {
        std::this_thread::sleep_for(kWatchdogInterval);
        continue;
      }
      last_frame_at = std::chrono::steady_clock::now();
    }

    avpacket_ptr pkt(av_packet_alloc(), avpacket_deleter{});
    const int read_result = av_read_frame(m_ctx.get(), pkt.get());

    if (read_result < 0) {
      const auto now = std::chrono::steady_clock::now();
      if (now - last_frame_at >= kWatchdogInterval) {
        std::cerr << "RTSP reader watchdog: reconnecting due to read error\n";
        disconnect();
      }
      continue;
    }

    if (!m_forward_stream_indexes.contains(pkt->stream_index))
      continue;

    last_frame_at = std::chrono::steady_clock::now();

    auto media = std::make_shared<media_packet>();
    media->packet = pkt;
    media->stream_index = pkt->stream_index;
    media->time_base = m_ctx->streams[pkt->stream_index]->time_base;
    media->media_type = m_ctx->streams[pkt->stream_index]->codecpar->codec_type;
    media->frame_rate = m_ctx->streams[pkt->stream_index]->avg_frame_rate;
    media->sample_rate = m_ctx->streams[pkt->stream_index]->codecpar->sample_rate;
    media->nb_samples = 0;
    avcodec_parameters_ptr codec_copy(avcodec_parameters_alloc(),
      [](AVCodecParameters* params) { avcodec_parameters_free(&params); });
    if (!codec_copy) {
      continue;
    }

    if (avcodec_parameters_copy(codec_copy.get(), m_ctx->streams[pkt->stream_index]->codecpar) < 0) {
      continue;
    }

    media->codec_parameters = std::move(codec_copy);

    media_sink* sink = nullptr;
    {
      std::scoped_lock lock(m_sink_mutex);
      sink = m_sink;
    }

    if (sink) {
      sink->on_packet(media);
    }
  }

  disconnect();
}
