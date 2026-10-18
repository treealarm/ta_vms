#pragma once

#include <memory>
#include <string>
#include <unordered_map>
#include <vector>

#include <grpcpp/grpcpp.h>

#include "vms_utils.grpc.pb.h"

class utils_client
{
public:
  utils_client();

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

  struct path_ref
  {
    std::string camera_id;
    std::string stream_name;
  };

  // Asks about every path in one call. Keyed by "camera_id/stream_name" in the result; a path the
  // response does not mention is absent from the map, which the caller treats as "no answer".
  // The whole map is empty when the RPC itself failed.
  std::unordered_map<std::string, path_info> get_mediamtx_paths(const std::vector<path_ref>& paths);

private:
  std::string m_app_id;
  std::shared_ptr<grpc::Channel> m_channel;
  std::unique_ptr<vms::utils::UtilsService::Stub> m_stub;
};
