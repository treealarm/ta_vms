#include "cfg_client.h"

#include <cstdlib>
#include <format>

#include <grpcpp/grpcpp.h>

#include "logger/logger.h"

namespace {
std::string read_env_or_default(const char* key, const char* fallback)
{
  const char* value = std::getenv(key);
  if (!value || *value == '\0') {
    return fallback;
  }

  return value;
}
}

cfg_client::cfg_client()
  :
    m_app_id(read_env_or_default("DAPR_CFG_APP_ID", "vmscfg")),
    m_channel(grpc::CreateChannel(
      read_env_or_default("DAPR_GRPC_ENDPOINT", "127.0.0.1:50001"),
      grpc::InsecureChannelCredentials())),
    m_stub(vms::cfg::CfgService::NewStub(m_channel))
{
}

bool cfg_client::list_cameras(std::vector<vms::cfg::CameraState>& result)
{
  vms::cfg::ListCamerasRequest request;
  vms::cfg::ListCamerasResponse response;

  grpc::ClientContext context;

  // опционально: таймаут
  context.set_deadline(
    std::chrono::system_clock::now() + std::chrono::seconds(2)
  );
  context.AddMetadata("dapr-app-id", m_app_id);

  grpc::Status status = m_stub->ListCameras(&context, request, &response);

  if (!status.ok())
  {
    logger::instance().error(std::format(
      "ListCameras RPC via Dapr failed for app '{}': {}",
      m_app_id,
      status.error_message()));

    return false;
  }

  result.reserve(response.cameras_size());

  for (const auto& cam : response.cameras())
  {
    result.push_back(cam); // копия proto-объекта
  }

  return true;
}
