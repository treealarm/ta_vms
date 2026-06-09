#include "proto_mapper.h"

recording_policy_spec proto_mapper::recording_policy_for_media(
  vms::cfg::RecordingMode grpc_mode,
  uint32_t prealarm_seg)
{
  recording_policy_spec pol;
  switch (grpc_mode)
  {
    case vms::cfg::RECORDING_CONTINUOUS:
      pol.mode = recording_policy_mode::continuous;
      pol.segment_len = std::chrono::seconds(60);
      return pol;
    case vms::cfg::RECORDING_PREALARM:
      pol.mode = recording_policy_mode::prealarm;
    {
      const uint32_t seg = prealarm_seg > 0 ? prealarm_seg : 5;
      pol.segment_len = std::chrono::seconds(seg);
      return pol;
    }
    case vms::cfg::RECORDING_OFF:
    default:
      pol.mode = recording_policy_mode::off;
      return pol;
  }
}

recording_policy_spec proto_mapper::recording_policy_for_stream(
  const vms::cfg::CameraStream& stream)
{
  // Use override profile if present, otherwise fall back to the base profile.
  const bool has_override = stream.has_override_profile();
  if (!has_override && !stream.has_recording_profile())
    return recording_policy_for_media(vms::cfg::RECORDING_OFF, 0);

  const auto& profile = has_override ? stream.override_profile() : stream.recording_profile();
  const uint32_t seg = profile.segment_seconds() > 0 ? profile.segment_seconds() : 5;
  return recording_policy_for_media(profile.mode(), seg);
}

uint32_t proto_mapper::prealarm_segment_seconds(const vms::control::CameraState& state)
{
  return state.segment_seconds() > 0 ? state.segment_seconds() : 5;
}
