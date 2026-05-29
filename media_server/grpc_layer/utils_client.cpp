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

int utils_client::get_mediamtx_readers(const std::string& camera_id, const std::string& stream_name)
{
  vms::utils::GetMediaMtxPathInfoRequest request;
  request.set_camera_id(camera_id);
  request.set_stream_name(stream_name);

  vms::utils::GetMediaMtxPathInfoResponse response;
  grpc::ClientContext context;
  context.set_deadline(std::chrono::system_clock::now() + std::chrono::seconds(2));
  context.AddMetadata("dapr-app-id", m_app_id);

  grpc::Status status = m_stub->GetMediaMtxPathInfo(&context, request, &response);

  if (!status.ok())
  {
    logger::instance().warning(std::format(
      "GetMediaMtxPathInfo RPC failed (app '{}'): {}", m_app_id, status.error_message()));
    return -1;
  }

  if (!response.success())
    return -1;

  return response.readers_count();
}
