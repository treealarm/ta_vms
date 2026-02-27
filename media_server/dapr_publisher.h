#pragma once

#include <memory>
#include <string>

#include <grpcpp/grpcpp.h>

#include "dapr/proto/runtime/v1/dapr.grpc.pb.h"

class dapr_publisher {
public:
  dapr_publisher();

  bool publish_json(const std::string& topic, const std::string& payload) const;

private:
  std::string m_pubsub_name;
  std::shared_ptr<grpc::Channel> m_channel;
  std::unique_ptr<dapr::proto::runtime::v1::Dapr::Stub> m_stub;
};
