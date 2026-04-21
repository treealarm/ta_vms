#include <chrono>
#include <thread>
#include "interfaces/av_deleters.h"
#include "rtsp_reader.h"
#include "grpc_layer/onvif_client.h"
#include "logger/logger.h"

namespace {
constexpr auto kWatchdogInterval = std::chrono::seconds(5);

std::string build_rtsp_url_with_auth(const std::string& url,
  const std::string& user,
  const std::string& pass)
{
  if (user.empty()) return url;

  const std::string prefix = "rtsp://";
  if (url.rfind(prefix, 0) != 0) return url;

  return prefix + user + ":" + pass + "@" + url.substr(prefix.size());
}
}

rtsp_reader::rtsp_reader(
  const std::string& cam_id, const std::string& user, const std::string& pass):
  m_cam_id(cam_id),
  m_user(user),
  m_password(pass)
{

}
rtsp_reader::~rtsp_reader() 
{
  stop();
  disconnect();
}

bool rtsp_reader::open(const std::string& url) 
{
  m_rtsp_url = url;
  
  return true;
}


bool rtsp_reader::connect() 
{
  disconnect();

  AVDictionary* opts = nullptr;
  av_dict_set(&opts, "rtsp_transport", "tcp", 0);
  av_dict_set(&opts, "rtsp_flags", "prefer_tcp", 0);
  av_dict_set(&opts, "timeout", "5000000", 0);
  av_dict_set(&opts, "rw_timeout", "5000000", 0);

  if (!m_user.empty()) 
  {
    av_dict_set(&opts, "user", m_user.c_str(), 0);
    av_dict_set(&opts, "password", m_password.c_str(), 0);
  }
  AVFormatContext* ctx = nullptr;

  if (m_rtsp_url.empty())
  {
    onvif_client c_client;

    c_client.get_uri(m_cam_id, m_rtsp_url);
  }

  std::string final_url = build_rtsp_url_with_auth(m_rtsp_url, m_user, m_password);

  if (avformat_open_input(&ctx, final_url.c_str(), nullptr, &opts) < 0)
  {
    logger::instance().error("Failed to open RTSP");
    av_dict_free(&opts);
    avformat_close_input(&ctx);
    return false;
  }

  m_ctx.reset(ctx);

  av_dict_free(&opts);

  if (avformat_find_stream_info(m_ctx.get(), nullptr) < 0)
  {
    logger::instance().error("Failed to get stream info");
    disconnect();
    return false;
  }

  m_video_index = -1;
  m_forward_stream_indexes.clear();
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
    logger::instance().error("No video stream");
    disconnect();
    return false;
  }

  logger::instance().info("RTSP connected");
  return true;
}

void rtsp_reader::disconnect() {
  m_video_index = -1;
  m_forward_stream_indexes.clear();
  m_ctx.reset();
}

void rtsp_reader::add_sink(std::shared_ptr<media_sink> sink) 
{
  m_sinks_helper.add_sink(sink);
}

void rtsp_reader::remove_sink(std::shared_ptr<media_sink> sink) 
{
  m_sinks_helper.remove_sink(sink);
}

AVStream* rtsp_reader::video_stream() const 
{
  if (m_ctx && m_video_index >= 0)
    return m_ctx->streams[m_video_index];
  return nullptr;
}

void rtsp_reader::start() 
{
  if (m_running) 
    return;
  m_running = true;
  m_thread = std::thread([this] { read_loop(); });
}

void rtsp_reader::stop()
{
  m_running = false;
  if (m_thread.joinable())
    m_thread.join();
}

void rtsp_reader::read_loop()
{
  auto last_frame_at = std::chrono::steady_clock::now();

  while (m_running) {
    if (!m_ctx || m_video_index < 0 || m_forward_stream_indexes.empty()) 
    {
      if (!connect()) 
      {
        std::this_thread::sleep_for(kWatchdogInterval);
        continue;
      }
      last_frame_at = std::chrono::steady_clock::now();
    }

    avpacket_ptr pkt(av_packet_alloc(), avpacket_deleter{});
    const int read_result = av_read_frame(m_ctx.get(), pkt.get());

    if (read_result < 0) 
    {
      const auto now = std::chrono::steady_clock::now();
      if (now - last_frame_at >= kWatchdogInterval)
      {
        logger::instance().warning("RTSP reader watchdog: reconnecting due to read error");
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

    if (avcodec_parameters_copy(codec_copy.get(), m_ctx->streams[pkt->stream_index]->codecpar) < 0)
    {
      continue;
    }

    media->codec_parameters = std::move(codec_copy);

    for (auto& s : m_sinks_helper.snapshot_sinks()) 
    {
      s->on_packet(media);
    }
  }

  disconnect();
}
