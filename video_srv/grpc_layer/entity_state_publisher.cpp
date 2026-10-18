#include "entity_state_publisher.h"

#include <format>

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

void entity_state_publisher::publish_stream_state(const camera_manager::stream_state& state) const
{
  const std::string state_json = std::format(
    R"({{"is_recording":{},"is_webrtc":{},"webrtc_enabled":{},"recording_policy":"{}","current_segment":"{}"}})",
    state.is_recording,
    state.is_web_rtc_streaming,
    state.is_web_rtc_enabled,
    to_string(state.recording_policy),
    state.current_segment);

  const std::string payload = std::format(
    R"({{"service":"vms","entity_type":"stream","entity_id":"{}/{}","state":{}}})",
    state.camera_id,
    state.stream_name,
    state_json);

  m_publisher.publish_json("debug-entity-state", payload);
}
