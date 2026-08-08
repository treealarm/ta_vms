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
  // What MediaMTX can currently say about one path.
  struct path_info
  {
    // False when the question could not be put at all — VmsUtils down, API unreachable. Says
    // nothing about viewers, so the caller must change nothing.
    bool answered = false;
    // False when the assigned server has no control API. Then viewers are unknowable by design and
    // publishing cannot be demand-driven.
    bool api_configured = false;
    int  readers = 0;
  };

  path_info get_mediamtx_path(const std::string& camera_id, const std::string& stream_name);

private:
  std::string m_app_id;
  std::shared_ptr<grpc::Channel> m_channel;
  std::unique_ptr<vms::utils::UtilsService::Stub> m_stub;
};
