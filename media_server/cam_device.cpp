#include "cam_device.h"

cam_device::cam_device(const std::string& camera_id)
  : m_camera_id(camera_id)
{}

cam_device::~cam_device()
{
  shutdown();
}

bool cam_device::add_stream(
  const std::string& stream_name,
  const std::string& profile_token,
  const std::string& rtsp_url,
  const std::string& user,
  const std::string& pass,
  const recording_policy_spec& policy,
  bool web_rtc,
  std::string& error,
  const std::string& webrtc_rtsp_push_url)
{
  auto it = m_streams.find(stream_name);
  if (it != m_streams.end())
  {
    it->second->shutdown();
    m_streams.erase(it);
  }

  try
  {
    auto stream = std::make_shared<cam_stream>(rtsp_url, m_camera_id, stream_name, profile_token, user, pass, webrtc_rtsp_push_url, web_rtc);
    stream->apply_recording_policy(policy);

    if (web_rtc)
    {
      if (!stream->start_web_rtc(error))
        return false;
    }

    m_streams.emplace(stream_name, std::move(stream));
    return true;
  }
  catch (const std::exception& ex)
  {
    error = ex.what();
    return false;
  }
}

bool cam_device::remove_stream(const std::string& stream_name, std::string& error)
{
  auto it = m_streams.find(stream_name);
  if (it == m_streams.end())
  {
    error = "stream not found: " + stream_name;
    return false;
  }
  it->second->shutdown();
  m_streams.erase(it);
  return true;
}

void cam_device::shutdown()
{
  for (auto& [_, stream] : m_streams)
    stream->shutdown();
  m_streams.clear();
}

cam_stream* cam_device::get_stream(const std::string& stream_name)
{
  auto it = m_streams.find(stream_name);
  return it != m_streams.end() ? it->second.get() : nullptr;
}

const cam_stream* cam_device::get_stream(const std::string& stream_name) const
{
  auto it = m_streams.find(stream_name);
  return it != m_streams.end() ? it->second.get() : nullptr;
}

cam_stream* cam_device::first_stream()
{
  return m_streams.empty() ? nullptr : m_streams.begin()->second.get();
}

const cam_stream* cam_device::first_stream() const
{
  return m_streams.empty() ? nullptr : m_streams.begin()->second.get();
}
