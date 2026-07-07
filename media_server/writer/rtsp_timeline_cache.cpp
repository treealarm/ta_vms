#include "rtsp_timeline_cache.h"

rtsp_timeline_cache& rtsp_timeline_cache::instance()
{
  static rtsp_timeline_cache cache;
  return cache;
}

bool rtsp_timeline_cache::restore(
  const std::string& rtsp_url,
  AVMediaType media_type,
  int64_t& last_dts,
  int64_t& last_pts)
{
  const auto now = std::chrono::steady_clock::now();
  std::lock_guard lock(m_mutex);

  auto timeline_it = m_timelines.find(rtsp_url);
  if (timeline_it == m_timelines.end()) {
    return false;
  }

  auto timing_it = timeline_it->second.find(static_cast<int>(media_type));
  if (timing_it == timeline_it->second.end()) {
    return false;
  }

  if (now - timing_it->second.updated_at > m_ttl) {
    timeline_it->second.erase(timing_it);
    if (timeline_it->second.empty()) {
      m_timelines.erase(timeline_it);
    }
    return false;
  }

  last_dts = timing_it->second.last_dts;
  last_pts = timing_it->second.last_pts;
  return true;
}

void rtsp_timeline_cache::persist(
  const std::string& rtsp_url,
  AVMediaType media_type,
  int64_t last_dts,
  int64_t last_pts)
{
  if (rtsp_url.empty() || last_dts == AV_NOPTS_VALUE || last_pts == AV_NOPTS_VALUE) {
    return;
  }

  std::lock_guard lock(m_mutex);
  m_timelines[rtsp_url][static_cast<int>(media_type)] = {
    .last_dts = last_dts,
    .last_pts = last_pts,
    .updated_at = std::chrono::steady_clock::now(),
  };
}

void rtsp_timeline_cache::invalidate(const std::string& rtsp_url)
{
  if (rtsp_url.empty())
  {
    return;
  }
  std::lock_guard lock(m_mutex);
  m_timelines.erase(rtsp_url);
}
