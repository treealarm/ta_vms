#pragma once

#include <cstdlib>
#include <string>

inline std::string resolve_webrtc_rtsp_base_url()
{
  if (const char* value = std::getenv("VMS_WEBRTC_RTSP_BASE_URL")) {
    if (value[0] != '\0') {
      return value;
    }
  }

  return "rtsp://localhost:8554";
}

inline std::string resolve_default_webrtc_rtsp_url(const std::string& camera_id)
{
  std::string base_url = resolve_webrtc_rtsp_base_url();

  if (!base_url.empty() && base_url.back() != '/') {
    base_url.push_back('/');
  }

  return base_url + camera_id;
}