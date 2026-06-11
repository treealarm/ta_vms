#pragma once

#include "camera_manager.h"
#include "dapr_publisher.h"

// Publishes debug "entity state" reports for camera streams to the
// debug-entity-state pub/sub topic (see CLAUDE.md).
class entity_state_publisher
{
public:
  void publish_stream_state(const camera_manager::stream_state& state) const;

private:
  dapr_publisher m_publisher;
};
