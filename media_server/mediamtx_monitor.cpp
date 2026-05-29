#include "mediamtx_monitor.h"

#include <format>

#include "logger/logger.h"

mediamtx_monitor::mediamtx_monitor(camera_manager& cameras, utils_client& utils,
                                   std::chrono::seconds idle_timeout)
  : m_cameras(cameras)
  , m_utils(utils)
  , m_idle_timeout(idle_timeout)
{}

mediamtx_monitor::~mediamtx_monitor()
{
  stop();
}

void mediamtx_monitor::start()
{
  m_stop = false;
  m_thread = std::thread(&mediamtx_monitor::poll_loop, this);
}

void mediamtx_monitor::stop()
{
  m_stop = true;
  if (m_thread.joinable())
    m_thread.join();
}

void mediamtx_monitor::poll_loop()
{
  while (!m_stop)
  {
    auto streams = m_cameras.webrtc_streams_snapshot();

    for (const auto& stream : streams)
    {
      const std::string path = stream->mediamtx_path();
      const int readers = m_utils.get_mediamtx_readers(
        stream->get_camera_id(), stream->get_stream_name());

      if (readers < 0)
        continue; // VmsUtils unavailable or server has no api_url — leave stream state as-is
                  // (stream stays running if webrtc=true in config, stays stopped otherwise)

      if (readers > 0)
      {
        m_last_active[path] = std::chrono::steady_clock::now();

        if (!stream->is_web_rtc_streaming())
        {
          std::string err;
          if (stream->start_web_rtc(err))
            logger::instance().info(std::format("mediamtx_monitor: started WebRTC for '{}'", path));
          else
            logger::instance().warning(std::format("mediamtx_monitor: failed to start WebRTC for '{}': {}", path, err));
        }
      }
      else
      {
        if (!stream->is_web_rtc_streaming())
          continue;

        auto it = m_last_active.find(path);
        auto idle_since = (it != m_last_active.end())
          ? it->second
          : std::chrono::steady_clock::now() - m_idle_timeout - std::chrono::seconds(1);

        if (std::chrono::steady_clock::now() - idle_since >= m_idle_timeout)
        {
          std::string err;
          if (stream->stop_web_rtc(err))
            logger::instance().info(std::format("mediamtx_monitor: stopped WebRTC for '{}' (idle)", path));
          else
            logger::instance().warning(std::format("mediamtx_monitor: failed to stop WebRTC for '{}': {}", path, err));
        }
      }
    }

    for (int i = 0; i < k_poll_interval.count() * 10 && !m_stop; ++i)
      std::this_thread::sleep_for(std::chrono::milliseconds(100));
  }
}
