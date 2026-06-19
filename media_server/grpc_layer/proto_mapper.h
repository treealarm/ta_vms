#pragma once

#include <cstdint>
#include <string>

#include "recording_types.h"
#include "vms_cfg.pb.h"
#include "vms_control.pb.h"

class proto_mapper
{
public:
  static recording_policy_spec recording_policy_for_media(
    vms::cfg::RecordingMode mode,
    uint32_t prealarm_segment_seconds);

  static recording_policy_spec recording_policy_for_stream(
    const vms::cfg::CameraStream& stream);

  // Resolves a stream's configured "path" into the final RTSP URL to connect with.
  // - profile_token set        -> "" (camera resolves its own URL via ONVIF at connect time)
  // - path empty                -> "" (unchanged behaviour)
  // - path already a full URL   -> used as-is, camera ip/port ignored
  // - path is a bare tail and camera has onvif_conn -> "rtsp://{ip}:{port}/{path}"
  // - otherwise                 -> path returned unchanged (lenient passthrough)
  static std::string resolve_stream_path(
    const vms::cfg::CameraStream& stream,
    const vms::cfg::CameraSetup& camera);

  static uint32_t prealarm_segment_seconds(const vms::control::CameraState& state);
};
