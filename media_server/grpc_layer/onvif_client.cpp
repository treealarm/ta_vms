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
    m_channel(grpc::CreateChannel(
      read_env_or_default("CFG_VMS_ADDR", "127.0.0.1:5135"),
      grpc::InsecureChannelCredentials())),
    m_stub(vms::onvif::OnvifService::NewStub(m_channel))
{
}

bool onvif_client::get_uri(const std::string& cam_id, std::string& uri)
{
  vms::cfg::SourceRequest request;
  request.set_source_id(cam_id);

  vms::onvif::OnvifState response;

  grpc::ClientContext context;
  context.set_deadline(
    std::chrono::system_clock::now() + std::chrono::seconds(10)
  );

  grpc::Status status = m_stub->GetCameraOnvifUri(&context, request, &response);

  if (!status.ok())
  {
    logger::instance().error(std::format("GetCameraOnvifUri RPC failed: {}", status.error_message()));

    return false;
  }

  uri = response.rtsp_url();
  return true;
}
