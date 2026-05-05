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

uint32_t proto_mapper::prealarm_segment_seconds(const vms::control::CameraState& state)
{
  return state.segment_seconds() > 0 ? state.segment_seconds() : 5;
}

uint32_t proto_mapper::prealarm_segment_seconds(const vms::cfg::CameraSetup& setup)
{
  if (setup.has_pre_alarm() && setup.pre_alarm().segment_seconds() > 0)
    return setup.pre_alarm().segment_seconds();
  return 5;
}
