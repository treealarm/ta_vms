#pragma once

#include <memory>
#include <string>

#include <grpcpp/grpcpp.h>

#include "dapr/proto/runtime/v1/dapr.grpc.pb.h"

class state_store_client {
public:
  state_store_client();

  void save_heartbeat();

private:
  std::string m_server_id;
  std::string m_store_name;
  std::shared_ptr<grpc::Channel> m_channel;
  std::unique_ptr<dapr::proto::runtime::v1::Dapr::Stub> m_stub;
};
