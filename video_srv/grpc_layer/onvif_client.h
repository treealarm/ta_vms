#pragma once

#include <memory>
#include <string>

#include <grpcpp/grpcpp.h>

#include "onvif.grpc.pb.h"

class onvif_client
{
public:
  onvif_client();
  bool get_uri(const std::string& cam_id, const std::string& profile_token, std::string& uri);
private:
  std::string m_app_id;
  std::shared_ptr<grpc::Channel> m_channel;
  std::unique_ptr<vms::onvif::OnvifService::Stub> m_stub;
};
