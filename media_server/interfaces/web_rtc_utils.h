#pragma once

#include <cstdlib>
#include <iomanip>
#include <sstream>
#include <string>

inline std::string resolve_webrtc_rtsp_base_url()
{
  const char* value = std::getenv("VMS_WEBRTC_RTSP_BASE_URL");
  return (value && value[0] != '\0') ? value : "";
}

namespace webrtc_rtsp_detail
{
  // Percent-encode everything that is not unreserved, so a password containing ':', '@' or '/'
  // cannot break the authority apart.
  inline std::string encode_userinfo(const std::string& raw)
  {
    std::ostringstream out;
    out << std::hex << std::uppercase << std::setfill('0');
    for (unsigned char c : raw)
    {
      if (std::isalnum(c) || c == '-' || c == '_' || c == '.' || c == '~')
        out << c;
      else
        out << '%' << std::setw(2) << static_cast<int>(c);
    }
    return out.str();
  }

  inline std::string with_userinfo(std::string base, const std::string& user, const std::string& pass)
  {
    if (user.empty())
      return base;

    const auto scheme_end = base.find("://");
    if (scheme_end == std::string::npos)
      return base;

    const auto authority_start = scheme_end + 3;
    const auto authority_end = base.find('/', authority_start);
    const auto authority = base.substr(authority_start, authority_end - authority_start);
    if (authority.find('@') != std::string::npos)
      return base;

    const auto encoded_user = encode_userinfo(user);
    const auto encoded_pass = pass.empty() ? std::string{} : ":" + encode_userinfo(pass);
    base.insert(authority_start, encoded_user + encoded_pass + "@");
    return base;
  }
}

// How this stream is published to MediaMTX for WebRTC viewing. The two settings come from the
// same place -- the router row in VmsCfg -- and are passed together so that neither can be given
// without the other, and so that two adjacent bools never end up in one signature.
struct webrtc_publish_config
{
  // Override for the MediaMTX RTSP push target; empty falls back to the environment.
  std::string rtsp_push_url;
  // Re-encode HEVC to H.264 before publishing. False means the camera's own H.265 is passed
  // through, which MediaMTX carries over WebRTC to any browser that can decode it.
  bool transcode_hevc = false;
};

// Build the RTSP push URL for a given camera/stream.
// If push_base_url is non-empty it takes precedence over the env var fallback.
//
// Credentials, when required, are already carried by the configured router's base URL.
// libavformat's RTSP muxer reads them from the URL; keep the result out of logs.
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