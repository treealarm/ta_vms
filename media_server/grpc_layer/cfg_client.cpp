#include "cfg_client.h"

#include <cstdlib>
#include <iostream>

#include <grpcpp/grpcpp.h>


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
    m_channel(grpc::CreateChannel(
      read_env_or_default("CFG_VMS_ADDR", "127.0.0.1:5135"),
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

  grpc::Status status = m_stub->ListCameras(&context, request, &response);

  if (!status.ok())
  {
    std::cerr << "ListCameras RPC failed: "
      << status.error_message() << std::endl;

    return false;
  }

  result.reserve(response.cameras_size());

  for (const auto& cam : response.cameras())
  {
    result.push_back(cam); // копия proto-объекта
  }

  return true;
}

bool cfg_client::get_uri(const std::string& cam_id, std::string& uri)
{
  vms::cfg::SourceRequest request;
  request.set_source_id(cam_id);

  vms::cfg::OnvifState response;

  grpc::ClientContext context;

  // опционально: таймаут
  context.set_deadline(
    std::chrono::system_clock::now() + std::chrono::seconds(10)
  );

  grpc::Status status = m_stub->GetCameraOnvifUri(&context, request, &response);

  if (!status.ok())
  {
    std::cerr << "GetCameraOnvifUri RPC failed: "
      << status.error_message() << std::endl;

    return false;
  }
  uri = response.rtsp_url();
  return true;
}