#pragma once

#include <chrono>
#include <cstdint>
#include <mutex>
#include <string>
#include <unordered_map>

extern "C" {
#include <libavutil/avutil.h>
}

class rtsp_timeline_cache {
public:
  static rtsp_timeline_cache& instance();

  rtsp_timeline_cache(const rtsp_timeline_cache&) = delete;
  rtsp_timeline_cache& operator=(const rtsp_timeline_cache&) = delete;

  bool restore(
    const std::string& rtsp_url,
    AVMediaType media_type,
    int64_t& last_dts,
    int64_t& last_pts);

  void persist(
    const std::string& rtsp_url,
    AVMediaType media_type,
    int64_t last_dts,
    int64_t last_pts);

private:
  struct stream_timing {
    int64_t last_dts = AV_NOPTS_VALUE;
    int64_t last_pts = AV_NOPTS_VALUE;
    std::chrono::steady_clock::time_point updated_at{};
  };

  rtsp_timeline_cache() = default;

  static constexpr auto m_ttl = std::chrono::seconds(3);

  std::mutex m_mutex;
  std::unordered_map<std::string, std::unordered_map<int, stream_timing>> m_timelines;
};
