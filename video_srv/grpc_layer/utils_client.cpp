#include "utils_client.h"

#include <cstdlib>
#include <format>

#include <grpcpp/grpcpp.h>

#include "logger/logger.h"

namespace {
std::string require_env(const char* key)
{
  const char* value = std::getenv(key);
  if (!value || *value == '\0')
    throw std::runtime_error(std::format("Required environment variable '{}' is not set", key));
  return value;
}
}

utils_client::utils_client()
  : m_app_id(require_env("DAPR_UTILS_APP_ID")),
    m_channel(grpc::CreateChannel("127.0.0.1:" + require_env("DAPR_GRPC_PORT"), grpc::InsecureChannelCredentials())),
    m_stub(vms::utils::UtilsService::NewStub(m_channel))
{
}

std::unordered_map<std::string, utils_client::path_info> utils_client::get_mediamtx_paths(
  const std::vector<path_ref>& paths)
{
  std::unordered_map<std::string, path_info> result;
  if (paths.empty())
    return result;

  vms::utils::GetMediaMtxPathsInfoRequest request;
  for (const auto& ref : paths)
  {
    auto* entry = request.add_paths();
    entry->set_camera_id(ref.camera_id);
    entry->set_stream_name(ref.stream_name);
  }

  vms::utils::GetMediaMtxPathsInfoResponse response;
  grpc::ClientContext context;
  // Scales with the batch rather than with one path: VmsUtils may have to resolve several servers
  // and list each of them before it can answer.
  context.set_deadline(std::chrono::system_clock::now() + std::chrono::seconds(10));
  context.AddMetadata("dapr-app-id", m_app_id);

  grpc::Status status = m_stub->GetMediaMtxPathsInfo(&context, request, &response);

  if (!status.ok())
  {
    logger::instance().warning(std::format(
      "GetMediaMtxPathsInfo RPC failed (app '{}'): {}", m_app_id, status.error_message()));
    return result;
  }

  for (const auto& info : response.paths())
  {
    if (!info.success())
      continue;

    result.emplace(
      info.camera_id() + "/" + info.stream_name(),
      path_info{ true, info.api_configured(), info.readers_count() });
  }

  return result;
}
