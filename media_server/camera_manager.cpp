#include "camera_manager.h"

#include <format>

#include "grpc_layer/proto_mapper.h"
#include "logger/logger.h"
#include "vms_control.pb.h"

bool camera_manager::configure_camera(
  const std::string& camera_id,
  const std::string& user,
  const std::string& pass,
  const std::vector<stream_config>& streams,
  std::string& error)
{
  std::scoped_lock lock(m_mutex);

  if (camera_id.empty())
  {
    error = "camera_id must not be empty";
    return false;
  }

  auto existing = m_cameras.find(camera_id);
  if (existing != m_cameras.end())
  {
    existing->second->shutdown();
    m_cameras.erase(existing);
  }

  if (streams.empty())
    return true;

  auto device = std::make_shared<cam_device>(camera_id);
  bool any_failed = false;

  for (const auto& s : streams)
  {
    std::string stream_error;
    if (!device->add_stream(s.stream_name, s.profile_token, s.rtsp_url, user, pass, s.policy, s.web_rtc, stream_error))
    {
      logger::instance().error(std::format(
        "Failed to add stream '{}' for camera '{}': {}", s.stream_name, camera_id, stream_error));
      any_failed = true;
    }
  }

  m_cameras.emplace(camera_id, std::move(device));

  if (any_failed)
  {
    error = "one or more streams failed to start";
    return false;
  }
  return true;
}

bool camera_manager::add_camera(
  const std::string& camera_id,
  const std::string& rtsp_url,
  const std::string& user,
  const std::string& pass,
  const recording_policy_spec& policy,
  bool web_rtc,
  std::string& error)
{
  std::scoped_lock lock(m_mutex);

  if (camera_id.empty())
  {
    error = "camera_id must not be empty";
    return false;
  }

  auto it = m_cameras.find(camera_id);
  if (it != m_cameras.end())
  {
    std::string dummy;
    it->second->remove_stream("", dummy);
  }
  else
  {
    it = m_cameras.emplace(camera_id, std::make_shared<cam_device>(camera_id)).first;
  }

  return it->second->add_stream("", "", rtsp_url, user, pass, policy, web_rtc, error);
}

bool camera_manager::start_recording(const std::string& camera_id, std::string& error)
{
  std::scoped_lock lock(m_mutex);

  auto it = m_cameras.find(camera_id);
  if (it == m_cameras.end())
  {
    error = "camera not found: " + camera_id;
    return false;
  }

  auto* stream = it->second->first_stream();
  if (!stream)
  {
    error = "camera has no streams: " + camera_id;
    return false;
  }

  if (stream->is_recording())
  {
    error = "camera already recording: " + camera_id;
    return false;
  }

  stream->apply_recording_policy(proto_mapper::recording_policy_for_media(
    vms::cfg::RECORDING_CONTINUOUS, 0));
  return true;
}

bool camera_manager::stop_recording(const std::string& camera_id, std::string& error)
{
  std::scoped_lock lock(m_mutex);

  auto it = m_cameras.find(camera_id);
  if (it == m_cameras.end())
  {
    error = "camera not found: " + camera_id;
    return false;
  }

  auto* stream = it->second->first_stream();
  if (!stream)
  {
    error = "camera has no streams: " + camera_id;
    return false;
  }

  if (!stream->is_recording())
  {
    error = "camera is not recording: " + camera_id;
    return false;
  }

  stream->stop_recording();
  return true;
}

std::optional<camera_manager::camera_info> camera_manager::get_camera_state(
  const std::string& camera_id,
  std::string& error)
{
  std::scoped_lock lock(m_mutex);

  auto it = m_cameras.find(camera_id);
  if (it == m_cameras.end())
  {
    error = "camera not found: " + camera_id;
    return std::nullopt;
  }

  auto* stream = it->second->first_stream();
  if (!stream)
  {
    error = "camera has no streams: " + camera_id;
    return std::nullopt;
  }

  return camera_info{
    .camera_id          = camera_id,
    .rtsp_url           = stream->get_source(),
    .user               = stream->get_user(),
    .pass               = stream->get_pass(),
    .is_recording       = stream->is_recording(),
    .is_web_rtc_streaming = stream->is_web_rtc_streaming(),
    .recording_policy   = stream->get_recording_policy_mode(),
  };
}

bool camera_manager::set_camera_state(
  const std::string& camera_id,
  const std::string& stream_name,
  const std::string& rtsp_url,
  const std::string& user,
  const std::string& pass,
  const recording_policy_spec& policy,
  bool web_rtc,
  std::string& error)
{
  std::scoped_lock lock(m_mutex);

  auto it = m_cameras.find(camera_id);
  if (it == m_cameras.end())
  {
    auto device = std::make_shared<cam_device>(camera_id);
    if (!device->add_stream(stream_name, "", rtsp_url, user, pass, policy, web_rtc, error))
      return false;
    m_cameras.emplace(camera_id, std::move(device));
    return true;
  }

  auto* stream = it->second->get_stream(stream_name);
  if (!stream)
    return it->second->add_stream(stream_name, "", rtsp_url, user, pass, policy, web_rtc, error);

  if (stream->get_source() != rtsp_url ||
      stream->get_user()   != user     ||
      stream->get_pass()   != pass)
  {
    std::string dummy;
    it->second->remove_stream(stream_name, dummy);
    return it->second->add_stream(stream_name, "", rtsp_url, user, pass, policy, web_rtc, error);
  }

  stream->apply_recording_policy(policy);

  if (web_rtc != stream->is_web_rtc_streaming())
  {
    if (web_rtc)
      return stream->start_web_rtc(error);
    else
      return stream->stop_web_rtc(error);
  }

  return true;
}

bool camera_manager::remove_camera(const std::string& camera_id, std::string& error)
{
  std::scoped_lock lock(m_mutex);

  auto it = m_cameras.find(camera_id);
  if (it == m_cameras.end())
  {
    error = "camera not found: " + camera_id;
    return false;
  }

  it->second->shutdown();
  m_cameras.erase(it);
  return true;
}

std::vector<camera_manager::camera_info> camera_manager::list_cameras() const
{
  std::scoped_lock lock(m_mutex);

  std::vector<camera_info> result;
  result.reserve(m_cameras.size());

  for (const auto& [camera_id, device] : m_cameras)
  {
    const auto* stream = device->first_stream();
    if (!stream) continue;

    result.push_back(camera_info{
      .camera_id            = camera_id,
      .rtsp_url             = stream->get_source(),
      .user                 = stream->get_user(),
      .pass                 = stream->get_pass(),
      .is_recording         = stream->is_recording(),
      .is_web_rtc_streaming = stream->is_web_rtc_streaming(),
      .recording_policy     = stream->get_recording_policy_mode(),
    });
  }

  return result;
}

void camera_manager::stop_all()
{
  std::scoped_lock lock(m_mutex);

  for (auto& [_, device] : m_cameras)
    device->shutdown();
}

