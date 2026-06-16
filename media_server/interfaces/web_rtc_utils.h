#pragma once

#include <cstdlib>
#include <stdexcept>
#include <string>

inline std::string resolve_webrtc_rtsp_base_url()
{
  const char* value = std::getenv("VMS_WEBRTC_RTSP_BASE_URL");
  return (value && value[0] != '\0') ? value : "";
}

// Build the RTSP push URL for a given camera/stream.
// If push_base_url is non-empty it takes precedence over the env var fallback.
inline std::string resolve_webrtc_rtsp_push_url(
  const std::string& camera_id,
  const std::string& stream_name,
  const std::string& push_base_url = "")
{
  std::string base = push_base_url.empty() ? resolve_webrtc_rtsp_base_url() : push_base_url;

  if (!base.empty() && base.back() != '/')
    base.push_back('/');

  if (stream_name.empty())
    return base + camera_id;

  return base + camera_id + "/" + stream_name;
}

// Legacy alias kept for callers that don't have an explicit push URL yet.
inline std::string resolve_default_webrtc_rtsp_url(
  const std::string& camera_id,
  const std::string& stream_name = "")
{
  return resolve_webrtc_rtsp_push_url(camera_id, stream_name, "");
}