#pragma once

#include <memory>
#include <string>
#include <vector>

#include <grpcpp/grpcpp.h>

#include "camera_manager.h"
#include "dapr/proto/runtime/v1/dapr.grpc.pb.h"

class state_store_client {
public:
  state_store_client();

  void save_heartbeat();

  /// Publishes what each stream is actually sending, one key per stream, for the camera catalogue
  /// to hand to the monitor. A snapshot of the last known state, not a history: the TTL is what
  /// makes a stale entry disappear, so a reader never has to judge an age itself, and a node that
  /// dies simply stops being represented.
  ///
  /// The browser deliberately does not subscribe to any of this — every slot would then depend on
  /// its own reconnect and race the catalogue.
  void save_stream_stats(const std::vector<camera_manager::stream_state>& streams, int ttl_seconds);

private:
  std::string m_server_id;
  std::string m_store_name;
  std::shared_ptr<grpc::Channel> m_channel;
  std::unique_ptr<dapr::proto::runtime::v1::Dapr::Stub> m_stub;
};
