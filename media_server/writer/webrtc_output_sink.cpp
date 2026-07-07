#include "writer/webrtc_output_sink.h"

#include <chrono>
#include <cstdlib>
#include <format>

extern "C" {
#include <libavcodec/avcodec.h>
}

#include "logger/logger.h"

namespace {
// Optional: VMS_WEBRTC_PUBLISH_AUDIO=0 keeps audio out of the WebRTC publish
// (A/V-sync diagnostics kill-switch). Unset or any other value means enabled.
bool webrtc_publish_audio_from_env()
{
  const char* raw = std::getenv("VMS_WEBRTC_PUBLISH_AUDIO");
  return raw == nullptr || std::string_view(raw) != "0";
}
}  // namespace

webrtc_output_sink::webrtc_output_sink(std::string rtsp_url)
  : m_publish_audio(webrtc_publish_audio_from_env())
{
  m_rtsp_output_sink = std::make_shared<rtsp_output_sink>(std::move(rtsp_url));
  m_video_compat_sink.add_sink(m_rtsp_output_sink);
  if (m_publish_audio)
    m_audio_compat_sink.add_sink(m_rtsp_output_sink);
  else
    logger::instance().info("webrtc_output_sink: audio publish disabled via VMS_WEBRTC_PUBLISH_AUDIO=0");

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
      // Close on the worker thread itself so sinks are torn down where they ran.
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
  if (media->media_type == AVMEDIA_TYPE_AUDIO && !m_publish_audio) {
    return;
  }

  const bool video_keyframe =
    media->media_type == AVMEDIA_TYPE_VIDEO &&
    media->packet &&
    (media->packet->flags & AV_PKT_FLAG_KEY) != 0;

  std::unique_lock lock(m_queue_mutex);

  // Never drop single packets from inside a GOP: the downstream transcoder's
  // decoder would lose reference frames and corrupt the picture until the next
  // keyframe (HEVC "Could not find ref with POC" errors). The only safe cut
  // points are keyframes, so backlog is shed in whole-GOP units.
  if (video_keyframe)
  {
    // A keyframe is a clean random-access point: if the backlog got too deep
    // (slow transcode, I/O hiccup), jump to live here instead of letting the
    // delay accumulate.
    if (m_dropping_until_keyframe || m_queue.size() >= m_drop_to_live_at)
    {
      logger::instance().warning(std::format(
        "webrtc_output_sink: drop-to-live at keyframe (backlog={}, was_dropping={})",
        m_queue.size(), m_dropping_until_keyframe));
      m_queue.clear();
    }
    m_dropping_until_keyframe = false;
  }
  else if (m_dropping_until_keyframe)
  {
    return;
  }
  else if (m_queue.size() >= m_max_queue)
  {
    // Queue is full mid-GOP: stop enqueueing (what is already queued is a
    // contiguous decodable run) and resume at the next keyframe.
    logger::instance().warning(std::format(
      "webrtc_output_sink: queue full ({}), dropping until next keyframe",
      m_queue.size()));
    m_dropping_until_keyframe = true;
    return;
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
