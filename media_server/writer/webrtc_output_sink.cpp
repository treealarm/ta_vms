#include "writer/webrtc_output_sink.h"

webrtc_output_sink::webrtc_output_sink(std::string rtsp_url)
  : m_rtsp_output_sink(std::move(rtsp_url))
{
  m_video_compat_sink.add_sink(&m_rtsp_output_sink);
  m_audio_compat_sink.add_sink(&m_rtsp_output_sink);

  m_worker = std::thread([this] {
    while (m_running) 
    {
      std::shared_ptr<media_packet> qp;
      {
        std::unique_lock lock(m_queue_mutex);

        m_cv.wait(lock, [&] {
          return !m_queue.empty() || !m_running;
          });

        if (!m_running)
          return;

        if (!m_queue.empty())
        {
          qp = std::move(m_queue.front());
          m_queue.pop_front();
        }        
      }
      send_to_sinks(qp);
    }
    });
}

webrtc_output_sink::~webrtc_output_sink()
{
  close();

  m_running = false;
  m_cv.notify_all();

  if (m_worker.joinable())
    m_worker.join();
}


void webrtc_output_sink::on_packet(const std::shared_ptr<media_packet>& media)
{
  if (!media) {
    return;
  }
  std::unique_lock lock(m_queue_mutex);

  if (m_queue.size() >= m_max_queue) {

    // ищем старый НЕключевой видео
    auto it = std::find_if(m_queue.begin(), m_queue.end(),
      [](const std::shared_ptr<media_packet>& p) {
        return p &&
          p->media_type == AVMEDIA_TYPE_VIDEO &&
          p->packet &&
          !(p->packet->flags & AV_PKT_FLAG_KEY);
      });

    if (it != m_queue.end()) {
      m_queue.erase(it);
    }
    else {
      m_queue.pop_front();
    }
  }

  m_queue.push_back(media);
  m_cv.notify_one();
}

void webrtc_output_sink::send_to_sinks(const std::shared_ptr<media_packet>& media)
{
  if (!media) {
    return;
  }

  if (media->media_type == AVMEDIA_TYPE_VIDEO) {
    m_video_compat_sink.on_packet(media);
    return;
  }

  if (media->media_type == AVMEDIA_TYPE_AUDIO) {
    m_audio_compat_sink.on_packet(media);
  }
}

void webrtc_output_sink::close()
{
  m_video_compat_sink.close();
  m_audio_compat_sink.close();
  m_rtsp_output_sink.close();
}
