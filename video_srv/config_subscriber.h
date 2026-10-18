#pragma once

#include <mutex>
#include <string>
#include <unordered_map>
#include <vector>

#include <grpcpp/grpcpp.h>

#include "camera_manager.h"
#include "grpc_layer/cfg_client.h"
#include "dapr/proto/runtime/v1/appcallback.grpc.pb.h"

// Implements Dapr AppCallback to receive pub/sub events.
// Subscribes to "server-config-changed" and reloads camera config when the event names this
// server. The event carries `{"server_id": "...", "camera_ids": [...]}`; the camera list is
// addressing for subscribers that act per camera, not state — here the whole config is re-read
// anyway, so only server_id is looked at.
class config_subscriber final : public dapr::proto::runtime::v1::AppCallback::Service
{
public:
  config_subscriber(camera_manager& cameras, cfg_client& cfg, std::string pubsub_name);

  grpc::Status ListTopicSubscriptions(
    grpc::ServerContext* context,
    const google::protobuf::Empty* request,
    dapr::proto::runtime::v1::ListTopicSubscriptionsResponse* response) override;

  grpc::Status OnTopicEvent(
    grpc::ServerContext* context,
    const dapr::proto::runtime::v1::TopicEventRequest* request,
    dapr::proto::runtime::v1::TopicEventResponse* response) override;

  // Fetches the current camera config from VmsCfg and reconciles video_srv's in-memory
  // camera set against it (respecting `disabled`). Used both on pub/sub events and at
  // startup, so there is a single place that knows how to build a camera from config.
  void reload_cameras();

private:
  camera_manager& m_cameras;
  cfg_client&     m_cfg;
  std::string     m_pubsub_name;

  static constexpr const char* k_topic = "server-config-changed";
  // A storage changed: repointed, taken out of service, removed. Not server-scoped -- a storage
  // is not owned by one node -- so unlike k_topic there is nothing in the payload to filter on,
  // and the only reaction is to stop trusting the cached answers.
  static constexpr const char* k_storage_topic = "storage-config-changed";
};
