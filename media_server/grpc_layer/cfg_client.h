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
  bool list_cameras(std::vector<vms::cfg::CameraSetup>& cfg);
  bool list_media_mtx_servers(std::vector<vms::cfg::MediaMtxServer>& result);
  bool get_paired_fs_id(std::string& fs_id);
private:
  std::string m_app_id;
  std::string m_server_id;
  std::shared_ptr<grpc::Channel> m_channel;
  std::unique_ptr<vms::cfg::CfgService::Stub> m_stub;
};
