#include "writer/webrtc_output_sink.h"

#include <chrono>
#include <format>

extern "C" {
#include <libavcodec/avcodec.h>
}

#include "logger/logger.h"

webrtc_output_sink::webrtc_output_sink(std::string rtsp_url)
{
  m_rtsp_output_sink = std::make_shared<rtsp_output_sink>(std::move(rtsp_url));
  m_video_compat_sink.add_sink(m_rtsp_output_sink);
  m_audio_compat_sink.add_sink(m_rtsp_output_sink);

  m_worker = std::thread([this] 
    {
      while (m_running)
      {
        std::shared_ptr<media_packet> qp;
        size_t depth = 0;
        {
          std::unique_lock lock(m_queue_mutex);

          m_cv.wait(lock, [&]
            {
              return !m_queue.empty() || !m_running;
            });

          if (!m_running)
            break;

          if (!m_queue.empty())
          {
            qp = std::move(m_queue.front());
            m_queue.pop_front();
            depth = m_queue.size();
          }
        }
        // DIAG: measure per-packet processing time (transcode + push to MediaMTX) and backlog.
        const auto t0 = std::chrono::steady_clock::now();
        send_to_sinks(qp);
        const auto ms = std::chrono::duration_cast<std::chrono::milliseconds>(
          std::chrono::steady_clock::now() - t0).count();
        if (ms > 40 || depth > 8)
          logger::instance().warning(std::format(
            "DIAG webrtc_output_sink: process={}ms backlog={} key={}",
            ms, depth,
            (qp && qp->packet) ? (int)((qp->packet->flags & AV_PKT_FLAG_KEY) != 0) : -1));
      }
      // Закрываем в том же потоке,  в каком работаем.
     close();
    });
}

webrtc_output_sink::~webrtc_output_sink()
{
  m_running = false;
  m_cv.notify_all();

  if (m_worker.joinable())
    m_worker.join();
}

void webrtc_output_sink::close()
{
  m_video_compat_sink.close();
  m_audio_compat_sink.close();
  m_rtsp_output_sink->close();
}

void webrtc_output_sink::on_packet(const std::shared_ptr<media_packet>& media)
{
  if (!media) {
    return;
  }
  std::unique_lock lock(m_queue_mutex);

  if (m_queue.size() >= m_max_queue) 
  {

    // ищем старый Ќ≈ключевой видео
    auto it = std::find_if(m_queue.begin(), m_queue.end(),
      [](const std::shared_ptr<media_packet>& p) 
      {
        return p &&
          p->media_type == AVMEDIA_TYPE_VIDEO &&
          p->packet &&
          !(p->packet->flags & AV_PKT_FLAG_KEY);
      });

    if (it != m_queue.end()) 
    {
      m_queue.erase(it);
    }
    else 
    {
      m_queue.pop_front();
    }
  }

  m_queue.push_back(media);
  m_cv.notify_one();
}

void webrtc_output_sink::send_to_sinks(const std::shared_ptr<media_packet>& media)
{
  if (!media)
  {
    logger::instance().warning("webrtc_output_sink: got empty packet from queue");
    return;
  }

  if (media->media_type == AVMEDIA_TYPE_VIDEO)
  {
    m_video_compat_sink.on_packet(media);
    return;
  }

  if (media->media_type == AVMEDIA_TYPE_AUDIO) 
  {
    m_audio_compat_sink.on_packet(media);
  }
}
