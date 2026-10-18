#include "writer/analytics_output_sink.h"

#include <algorithm>
#include <format>
#include <vector>

extern "C" {
#include <libavcodec/avcodec.h>
}

#include "logger/logger.h"

analytics_output_sink::analytics_output_sink(std::string rtsp_url, uint32_t sample_fps)
  : m_emit_period(std::chrono::milliseconds(1000 / (sample_fps > 0 ? sample_fps : 1)))
{
  m_rtsp_output_sink = std::make_shared<rtsp_output_sink>(std::move(rtsp_url));
  m_worker = std::thread([this] { worker_loop(); });
}

analytics_output_sink::~analytics_output_sink()
{
  m_running = false;
  m_cv.notify_all();
  if (m_worker.joinable())
    m_worker.join();
  if (m_rtsp_output_sink)
    m_rtsp_output_sink->close();
}

void analytics_output_sink::on_packet(const std::shared_ptr<media_packet>& media)
{
  if (!media || !media->packet)
    return;
  // Audio never reaches a detector, so it is dropped before the queue rather than after it.
  if (media->media_type != AVMEDIA_TYPE_VIDEO)
    return;

  const bool is_key = (media->packet->flags & AV_PKT_FLAG_KEY) != 0;

  // Keyframes are timed whatever the mode: the measurement is what chooses the mode.
  if (is_key)
  {
    note_keyframe();
    update_mode();
  }

  if (!m_send_all && !is_key)
    return;

  {
    std::scoped_lock lock(m_queue_mutex);
    if (is_key)
    {
      // A keyframe is a clean point to resume from, whatever went wrong before it.
      m_dropping_until_keyframe = false;
    }
    else if (m_dropping_until_keyframe)
    {
      return;
    }
    else if (m_queue.size() >= m_max_queue)
    {
      // Full mid-GOP: what is already queued is a contiguous decodable run, so stop adding to it
      // and pick up again at the next keyframe rather than handing the decoder a hole.
      logger::instance().warning(std::format(
        "analytics_output_sink: queue full ({}), dropping until next keyframe", m_queue.size()));
      m_dropping_until_keyframe = true;
      return;
    }
    m_queue.push_back(media);
  }
  m_cv.notify_one();
}

void analytics_output_sink::worker_loop()
{
  while (m_running)
  {
    std::shared_ptr<media_packet> pkt;
    {
      std::unique_lock lock(m_queue_mutex);
      m_cv.wait(lock, [&] { return !m_queue.empty() || !m_running; });
      if (!m_running)
        break;
      pkt = std::move(m_queue.front());
      m_queue.pop_front();
    }
    if (pkt && m_rtsp_output_sink)
      m_rtsp_output_sink->on_packet(pkt);
  }
}

void analytics_output_sink::note_keyframe()
{
  const auto now = std::chrono::steady_clock::now();
  m_keyframe_times.push_back(now);
  while (m_keyframe_times.size() > k_interval_gaps + 1)
    m_keyframe_times.pop_front();

  if (m_keyframe_times.size() < 2)
    return;

  std::vector<std::chrono::milliseconds> gaps;
  gaps.reserve(m_keyframe_times.size() - 1);
  for (size_t i = 1; i < m_keyframe_times.size(); ++i)
    gaps.push_back(std::chrono::duration_cast<std::chrono::milliseconds>(
      m_keyframe_times[i] - m_keyframe_times[i - 1]));
  std::sort(gaps.begin(), gaps.end());
  m_keyframe_interval = gaps[gaps.size() / 2];
}

void analytics_output_sink::update_mode()
{
  if (m_keyframe_interval.count() == 0)
    return;  // nothing measured yet, and keyframe-only is the answer we would have chosen anyway

  const auto enter = m_emit_period * 3 / 2;
  const auto leave = m_emit_period * 11 / 10;

  const bool want_all = m_send_all ? (m_keyframe_interval > leave)
                                   : (m_keyframe_interval > enter);
  if (want_all == m_send_all)
    return;

  m_send_all = want_all;
  logger::instance().info(std::format(
    "analytics_output_sink: keyframe interval {} ms vs sample period {} ms - sending {}",
    m_keyframe_interval.count(), m_emit_period.count(),
    m_send_all ? "every packet" : "keyframes only"));
}
