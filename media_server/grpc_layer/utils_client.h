#pragma once

#include <memory>
#include <string>

#include <grpcpp/grpcpp.h>

#include "vms_utils.grpc.pb.h"

class utils_client
{
public:
  utils_client();

  // Returns the number of WebRTC/RTSP readers for the given camera stream.
  // Returns -1 if UtilsService is unavailable or the request fails.
  int get_mediamtx_readers(const std::string& camera_id, const std::string& stream_name);

private:
  std::string m_app_id;
  std::shared_ptr<grpc::Channel> m_channel;
  std::unique_ptr<vms::utils::UtilsService::Stub> m_stub;
};
