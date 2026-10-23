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
  const std::string& server_id() const { return m_server_id; }
  bool list_cameras(std::vector<vms::cfg::CameraSetup>& cfg);
  bool list_media_mtx_servers(std::vector<vms::cfg::MediaMtxServer>& result);
  /// This node's partners, as VmsCfg declares them in MEDIA_SERVER_PAIRS: the FS and the ONVIF
  /// service next to this media server. False (and logged) until VmsCfg lists this server with both.
  bool get_own_node(std::string& fs_id, std::string& onvif_id);
private:
  std::string m_app_id;
  std::string m_server_id;
  std::shared_ptr<grpc::Channel> m_channel;
  std::unique_ptr<vms::cfg::CfgService::Stub> m_stub;
};
