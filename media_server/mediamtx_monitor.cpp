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

    // One call for the whole set. Asking per stream made the request rate — and the load it put on
    // VmsUtils, VmsCfg and the MediaMTX control API — grow with the number of published streams.
    std::vector<utils_client::path_ref> refs;
    refs.reserve(streams.size());
    for (const auto& stream : streams)
      refs.push_back({ stream->get_camera_id(), stream->get_stream_name() });

    const auto infos = m_utils.get_mediamtx_paths(refs);

    for (const auto& stream : streams)
    {
      // mediamtx_path() is "camera_id/stream_name", which is how the batch keys its answers.
      const std::string path = stream->mediamtx_path();
      const auto info_it = infos.find(path);
      const auto info = info_it != infos.end() ? info_it->second : utils_client::path_info{};

      // Nothing was learned this round — VmsUtils or the API is unreachable. Silence is not an
      // answer about viewers, so the stream is left exactly as it is.
      if (!info.answered)
        continue;

      // No control API on the assigned server: who is watching cannot be known, so the path is kept
      // published for as long as the camera has WebRTC enabled. The alternative would be to stop on
      // a guess and leave viewers with nothing to connect to.
      if (!info.api_configured)
      {
        if (!stream->is_web_rtc_streaming())
        {
          std::string err;
          if (stream->start_web_rtc(err))
            logger::instance().info(std::format(
              "mediamtx_monitor: started WebRTC for '{}' (server has no control API — always on)", path));
          else
            logger::instance().warning(std::format(
              "mediamtx_monitor: failed to start WebRTC for '{}': {}", path, err));
        }
        continue;
      }

      // From here on viewers are knowable, so publishing follows them.
      if (info.readers > 0)
      {
        m_last_active[path] = std::chrono::steady_clock::now();

        if (!stream->is_web_rtc_streaming())
        {
          std::string err;
          if (stream->start_web_rtc(err))
            logger::instance().info(std::format(
              "mediamtx_monitor: started WebRTC for '{}' ({} viewer(s))", path, info.readers));
          else
            logger::instance().warning(std::format("mediamtx_monitor: failed to start WebRTC for '{}': {}", path, err));
        }
        continue;
      }

      // Nobody is watching. A stopped stream stays stopped: MediaMTX creates the path as soon as a
      // viewer asks for it, that shows up as a reader here, and publishing starts on the next poll.
      // Starting it now instead — which is what this used to do — put the stream straight back into
      // the idle timeout and produced an endless start/stop cycle, burning an encoder on a picture
      // nobody was looking at while still leaving gaps when the path was down.
      if (!stream->is_web_rtc_streaming())
      {
        m_last_active.erase(path);
        continue;
      }

      auto it = m_last_active.find(path);
      if (it == m_last_active.end())
      {
        // Publishing but never yet watched: start the idle clock now rather than treat the stream
        // as already past the timeout.
        m_last_active[path] = std::chrono::steady_clock::now();
        continue;
      }

      if (std::chrono::steady_clock::now() - it->second >= m_idle_timeout)
      {
        std::string err;
        if (stream->stop_web_rtc(err))
        {
          m_last_active.erase(it);
          logger::instance().info(std::format("mediamtx_monitor: stopped WebRTC for '{}' (idle)", path));
        }
        else
        {
          logger::instance().warning(std::format("mediamtx_monitor: failed to stop WebRTC for '{}': {}", path, err));
        }
      }
    }

    for (int i = 0; i < k_poll_interval.count() * 10 && !m_stop; ++i)
      std::this_thread::sleep_for(std::chrono::milliseconds(100));
  }
}
