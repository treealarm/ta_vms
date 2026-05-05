#pragma once

#include <cstdint>

#include "recording_types.h"
#include "vms_cfg.pb.h"
#include "vms_control.pb.h"

class proto_mapper
{
public:
  static recording_policy_spec recording_policy_for_media(
    vms::cfg::RecordingMode mode,
    uint32_t prealarm_segment_seconds);

  static uint32_t prealarm_segment_seconds(const vms::control::CameraState& state);
  static uint32_t prealarm_segment_seconds(const vms::cfg::CameraSetup& setup);
};
