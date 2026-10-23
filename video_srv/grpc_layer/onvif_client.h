#pragma once

#include <memory>
#include <string>

#include <grpcpp/grpcpp.h>

#include "onvif.grpc.pb.h"

/// GetStreamUri on this node's own VmsOnvif: only that instance reaches the node's cameras. Which
/// one that is comes from the pairing in VmsCfg (cfg_client::get_own_node), set once at startup.
/// A reader may already be connecting by then (a config event can load cameras first); get_uri
/// fails until it is set, and the reader retries as it does for any unreachable source.
class onvif_client
{
public:
  static void set_app_id(const std::string& id);

  onvif_client();
  bool get_uri(const std::string& cam_id, const std::string& profile_token, std::string& uri);
private:
  std::shared_ptr<grpc::Channel> m_channel;
  std::unique_ptr<vms::onvif::OnvifService::Stub> m_stub;
};
