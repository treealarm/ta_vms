#include "onvif_client.h"

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

onvif_client::onvif_client()
  :
    m_app_id(read_env_or_default("DAPR_ONVIF_APP_ID", "vmsonvif")),
    m_channel(grpc::CreateChannel(
      read_env_or_default("DAPR_GRPC_ENDPOINT", "127.0.0.1:50001"),
      grpc::InsecureChannelCredentials())),
    m_stub(vms::onvif::OnvifService::NewStub(m_channel))
{
}

bool onvif_client::get_uri(const std::string& cam_id, const std::string& profile_token, std::string& uri)
{
  vms::onvif::StreamUriRequest request;
  request.set_source_id(cam_id);
  if (!profile_token.empty())
    request.set_profile_token(profile_token);

  vms::onvif::StreamUriResponse response;

  grpc::ClientContext context;
  context.set_deadline(
    std::chrono::system_clock::now() + std::chrono::seconds(10)
  );
  context.AddMetadata("dapr-app-id", m_app_id);

  grpc::Status status = m_stub->GetStreamUri(&context, request, &response);

  if (!status.ok())
  {
    logger::instance().error(std::format(
      "GetStreamUri RPC via Dapr failed for app '{}': {}",
      m_app_id,
      status.error_message()));

    return false;
  }

  uri = response.rtsp_url();
  return true;
}
