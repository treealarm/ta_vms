#pragma once

#include <memory>
#include <string>

#include <grpcpp/grpcpp.h>

#include "vms_cfg.grpc.pb.h"

using grpc::Channel;
using grpc::ClientContext;
using grpc::Status;

class cfg_client 
{
public:
  cfg_client();
  bool list_cameras(std::vector<vms::cfg::CameraState>& cfg);
private:
  std::string m_app_id;
  std::shared_ptr<grpc::Channel> m_channel;
  std::unique_ptr<vms::cfg::CfgService::Stub> m_stub;
};
