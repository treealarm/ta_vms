#include "camera_manager.h"

#include <format>
#include <unordered_map>
#include <vector>

#include "grpc_layer/proto_mapper.h"
#include "logger/logger.h"
#include "vms_control.pb.h"

namespace {
const char* to_string(recording_policy_mode mode)
{
  switch (mode)
  {
    case recording_policy_mode::off:        return "off";
    case recording_policy_mode::continuous: return "continuous";
    case recording_policy_mode::prealarm:   return "prealarm";
  }
  return "unknown";
}
}

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

  // New camera — just create it from scratch.
  if (existing == m_cameras.end())
  {
    if (streams.empty())
      return true;

    auto device = std::make_shared<cam_device>(camera_id);
    bool any_failed = false;
    for (const auto& s : streams)
    {
      std::string stream_error;
      if (!device->add_stream(s.stream_name, s.profile_token, s.rtsp_url, user, pass, s.policy, s.web_rtc, stream_error, s.webrtc))
      {
        logger::instance().error(std::format(
          "Failed to add stream '{}' for camera '{}': {}", s.stream_name, camera_id, stream_error));
        any_failed = true;
      }
    }
    m_cameras.emplace(camera_id, std::move(device));
    logger::instance().info(std::format(
      "Camera '{}' configured ({} stream(s))", camera_id, streams.size()));
    if (any_failed) { error = "one or more streams failed to start"; return false; }
    return true;
  }

  // Existing camera — apply changes incrementally to avoid unnecessary RTSP reconnects.
  auto& device = existing->second;
  bool any_failed = false;

  // Build lookup of desired streams.
  std::unordered_map<std::string, const stream_config*> desired;
  for (const auto& s : streams)
    desired[s.stream_name] = &s;

  // Remove streams no longer in the config.
  std::vector<std::string> to_remove;
  for (const auto& [name, _] : device->streams())
    if (!desired.count(name))
      to_remove.push_back(name);
  for (const auto& name : to_remove)
  {
    std::string stream_error;
    device->remove_stream(name, stream_error);
    logger::instance().info(std::format(
      "Stream '{}' removed for camera '{}'", name, camera_id));
  }

  // Add or update each desired stream.
  for (const auto& s : streams)
  {
    auto* stream = device->get_stream(s.stream_name);

    if (!stream)
    {
      // New stream on an existing device.
      std::string stream_error;
      if (!device->add_stream(s.stream_name, s.profile_token, s.rtsp_url, user, pass, s.policy, s.web_rtc, stream_error, s.webrtc))
      {
        logger::instance().error(std::format(
          "Failed to add stream '{}' for camera '{}': {}", s.stream_name, camera_id, stream_error));
        any_failed = true;
      }
      else
      {
        logger::instance().info(std::format(
          "Stream '{}' added for camera '{}'", s.stream_name, camera_id));
      }
      continue;
    }

    // RTSP source / credentials changed — must reconnect.
    // Only compare non-empty fields: VmsCfg stores "" for ONVIF cameras (URL is resolved
    // at runtime by LiveController via ONVIF discovery). An empty value in VmsCfg must
    // NOT be treated as "URL changed" — that would trigger a reconnect with an empty URL,
    // causing a 5-second libav timeout while m_mutex is held.
    const bool source_changed  = !s.rtsp_url.empty()      && stream->get_source()        != s.rtsp_url;
    const bool token_changed   = !s.profile_token.empty() && stream->get_profile_token() != s.profile_token;
    const bool user_changed    = stream->get_user() != user;
    const bool pass_changed    = stream->get_pass() != pass;
    if (source_changed || token_changed || user_changed || pass_changed)
    {
      std::string dummy;
      device->remove_stream(s.stream_name, dummy);
      std::string stream_error;
      if (!device->add_stream(s.stream_name, s.profile_token, s.rtsp_url, user, pass, s.policy, s.web_rtc, stream_error, s.webrtc))
      {
        logger::instance().error(std::format(
          "Failed to restart stream '{}' for camera '{}': {}", s.stream_name, camera_id, stream_error));
        any_failed = true;
      }
      else
      {
        logger::instance().info(std::format(
          "Stream '{}' for camera '{}' restarted (source/credentials changed)", s.stream_name, camera_id));
      }
      continue;
    }

    // Only the WebRTC publish settings changed — update in-place without RTSP reconnect.
    // Use the stream's current streaming state, not s.web_rtc: VmsCfg does not store
    // WebRTC-enabled state (that is owned by LiveController), so s.web_rtc is always
    // false from reload_cameras and would incorrectly kill live video.
    //
    // transcode_hevc is compared too: it decides which processor compat_sink builds, so a change
    // to it has to restart the publish or the router's new setting would not reach the stream
    // until something else happened to restart it.
    // The empty-URL guard covers the URL only. A deployment whose push target comes from
    // VMS_WEBRTC_RTSP_BASE_URL always sends an empty one, and gating the whole comparison on it
    // meant the router's transcode switch never reached the stream there; cam_stream keeps the
    // URL it has.
    const auto& current = stream->get_webrtc_config();
    const bool url_changed = !s.webrtc.rtsp_push_url.empty()
      && current.rtsp_push_url != s.webrtc.rtsp_push_url;
    if (url_changed || current.transcode_hevc != s.webrtc.transcode_hevc)
    {
      std::string dummy;
      stream->update_webrtc_publish(
        s.webrtc, stream->is_web_rtc_streaming(), dummy);
      logger::instance().info(std::format(
        "Stream '{}' for camera '{}' WebRTC publish settings updated (transcode_hevc={})",
        s.stream_name, camera_id, s.webrtc.transcode_hevc));
    }

    // Recording policy — hot-update, no reconnect needed.
    // WebRTC lifecycle (start/stop) is managed exclusively by LiveController via
    // set_camera_state / ensure_web_rtc. Do NOT touch WebRTC here: VmsCfg does not
    // persist the WebRTC-enabled flag, so managing it from config reload would kill
    // live video on every config change.
    stream->apply_recording_policy(s.policy);
  }

  if (any_failed) { error = "one or more streams failed to start"; return false; }
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
  const recording_policy_spec& policy,
  std::optional<bool> web_rtc,
  std::string& error)
{
  std::scoped_lock lock(m_mutex);

  auto it = m_cameras.find(camera_id);
  if (it == m_cameras.end())
  {
    error = "camera not found: " + camera_id;
    return false;
  }

  auto* stream = it->second->get_stream(stream_name);
  if (!stream)
  {
    error = "stream not found: " + stream_name;
    return false;
  }

  stream->apply_recording_policy(policy);

  bool ok = true;
  if (web_rtc.has_value())
  {
    stream->set_web_rtc_enabled(*web_rtc);
    if (*web_rtc != stream->is_web_rtc_streaming())
      ok = *web_rtc ? stream->start_web_rtc(error) : stream->stop_web_rtc(error);
  }

  if (ok)
    logger::instance().info(std::format(
      "Camera '{}' stream '{}' state updated: recording={}, web_rtc={}",
      camera_id, stream_name, to_string(policy.mode),
      web_rtc.has_value() ? (*web_rtc ? "true" : "false") : "unchanged"));

  return ok;
}

bool camera_manager::remove_camera(const std::string& camera_id)
{
  std::scoped_lock lock(m_mutex);

  auto it = m_cameras.find(camera_id);
  if (it == m_cameras.end())
    return true;

  it->second->shutdown();
  m_cameras.erase(it);
  logger::instance().info(std::format("Camera '{}' removed", camera_id));
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

void camera_manager::close_segments_on_source_loss()
{
  std::scoped_lock lock(m_mutex);
  for (const auto& [camera_id, device] : m_cameras)
  {
    for (const auto& [stream_name, stream] : device->streams())
    {
      if (!stream->liveness().alive)
      {
        stream->close_segment_on_source_loss();
      }
    }
  }
}

std::vector<camera_manager::stream_state> camera_manager::list_stream_states() const
{
  std::scoped_lock lock(m_mutex);

  std::vector<stream_state> result;
  for (const auto& [camera_id, device] : m_cameras)
  {
    for (const auto& [stream_name, stream] : device->streams())
    {
      const auto live = stream->liveness();
      result.push_back(stream_state{
        .camera_id            = camera_id,
        .stream_name          = stream_name,
        .is_recording         = stream->is_recording(),
        .is_web_rtc_streaming = stream->is_web_rtc_streaming(),
        .is_web_rtc_enabled   = stream->is_web_rtc_enabled(),
        .recording_policy     = stream->get_recording_policy_mode(),
        .current_segment      = stream->current_segment_path(),
        .source_alive         = live.alive,
        .last_frame_ts        = live.last_frame_ts,
        .recovered_frame_ts   = live.recovered_frame_ts,
        .video                = stream->observed_video_params(),
        .video_encoder        = stream->video_encoder_name(),
      });
    }
  }

  return result;
}

bool camera_manager::ensure_web_rtc(const std::string& camera_id,
                                     const std::string& stream_name,
                                     std::string& error)
{
  std::scoped_lock lock(m_mutex);

  auto it = m_cameras.find(camera_id);
  if (it == m_cameras.end())
  {
    error = "camera not found: " + camera_id;
    return false;
  }

  auto* stream = it->second->get_stream(stream_name);
  if (!stream)
  {
    error = "stream not found: " + stream_name;
    return false;
  }

  if (stream->is_web_rtc_streaming())
    return true;

  const bool ok = stream->start_web_rtc(error);
  if (ok)
    logger::instance().info(std::format(
      "Camera '{}' stream '{}' WebRTC streaming started", camera_id, stream_name));
  return ok;
}

bool camera_manager::ensure_analytics_push(const std::string& camera_id,
                                           const std::string& stream_name,
                                           const std::string& rtsp_url,
                                           uint32_t sample_fps,
                                           std::string& error)
{
  std::scoped_lock lock(m_mutex);

  auto it = m_cameras.find(camera_id);
  if (it == m_cameras.end())
  {
    error = "camera not found: " + camera_id;
    return false;
  }

  auto* stream = it->second->get_stream(stream_name);
  if (!stream)
  {
    error = "stream not found: " + stream_name;
    return false;
  }

  const bool ok = stream->start_analytics_push(rtsp_url, sample_fps, error);
  if (ok)
    logger::instance().info(std::format(
      "Camera '{}' stream '{}' analytics push -> {} ({} fps wanted)",
      camera_id, stream_name, rtsp_url, sample_fps));
  return ok;
}

bool camera_manager::stop_analytics_push(const std::string& camera_id,
                                         const std::string& stream_name,
                                         std::string& error)
{
  std::scoped_lock lock(m_mutex);

  auto it = m_cameras.find(camera_id);
  if (it == m_cameras.end())
  {
    error = "camera not found: " + camera_id;
    return false;
  }

  auto* stream = it->second->get_stream(stream_name);
  if (!stream)
  {
    error = "stream not found: " + stream_name;
    return false;
  }

  const bool ok = stream->stop_analytics_push(error);
  if (ok)
    logger::instance().info(std::format(
      "Camera '{}' stream '{}' analytics push stopped", camera_id, stream_name));
  return ok;
}

std::vector<std::shared_ptr<cam_stream>> camera_manager::webrtc_streams_snapshot() const
{
  std::scoped_lock lock(m_mutex);
  std::vector<std::shared_ptr<cam_stream>> result;
  for (const auto& [_, device] : m_cameras)
    for (const auto& [__, stream] : device->streams())
      if (stream->is_web_rtc_enabled())
        result.push_back(stream);
  return result;
}

void camera_manager::stop_all()
{
  std::scoped_lock lock(m_mutex);

  for (auto& [_, device] : m_cameras)
    device->shutdown();
}

