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
// Subscribes to "media-server-config-changed" and reloads camera config
// when the event payload matches this server's APP_ID.
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

private:
  void reload_cameras();

  camera_manager& m_cameras;
  cfg_client&     m_cfg;
  std::string     m_pubsub_name;

  static constexpr const char* k_topic = "media-server-config-changed";
};
