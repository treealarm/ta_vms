#include "onvif_client.h"

#include <cstdlib>
#include <format>
#include <mutex>

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

// Written once by main(), read by every reader thread.
std::mutex s_app_id_mutex;
std::string s_app_id;

std::string app_id()
{
  std::lock_guard<std::mutex> lock(s_app_id_mutex);
  return s_app_id;
}
}

void onvif_client::set_app_id(const std::string& id)
{
  std::lock_guard<std::mutex> lock(s_app_id_mutex);
  s_app_id = id;
}

onvif_client::onvif_client()
  : m_channel(grpc::CreateChannel("127.0.0.1:" + require_env("DAPR_GRPC_PORT"), grpc::InsecureChannelCredentials())),
    m_stub(vms::onvif::OnvifService::NewStub(m_channel))
{
}

bool onvif_client::get_uri(const std::string& cam_id, const std::string& profile_token, std::string& uri)
{
  const std::string target = app_id();
  if (target.empty())
  {
    logger::instance().warning(std::format(
      "GetStreamUri for '{}': this node's ONVIF service is not known yet (waiting for its pairing in VmsCfg)",
      cam_id));
    return false;
  }

  vms::onvif::StreamUriRequest request;
  request.set_source_id(cam_id);
  if (!profile_token.empty())
    request.set_profile_token(profile_token);

  vms::onvif::StreamUriResponse response;

  grpc::ClientContext context;
  context.set_deadline(
    std::chrono::system_clock::now() + std::chrono::seconds(10)
  );
  context.AddMetadata("dapr-app-id", target);

  grpc::Status status = m_stub->GetStreamUri(&context, request, &response);

  if (!status.ok())
  {
    logger::instance().error(std::format(
      "GetStreamUri RPC via Dapr failed for app '{}': {}",
      target,
      status.error_message()));

    return false;
  }

  uri = response.rtsp_url();
  return true;
}
