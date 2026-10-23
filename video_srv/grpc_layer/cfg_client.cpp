#include "cfg_client.h"

#include <cstdlib>
#include <format>

#include <grpcpp/grpcpp.h>

#include "logger/logger.h"

namespace {
std::string require_env(const char* key)
{
  const char* value = std::getenv(key);
  if (!value || *value == '\0')
  {
    const char* app_id = std::getenv("APP_ID");
    throw std::runtime_error(std::format(
      "Required environment variable '{}' is not set (APP_ID={})",
      key, app_id ? app_id : "<not set>"));
  }
  return value;
}
}

cfg_client::cfg_client()
  : m_app_id(require_env("DAPR_CFG_APP_ID")),
    m_server_id(require_env("APP_ID")),
    m_channel(grpc::CreateChannel("127.0.0.1:" + require_env("DAPR_GRPC_PORT"), grpc::InsecureChannelCredentials())),
    m_stub(vms::cfg::CfgService::NewStub(m_channel))
{
}

bool cfg_client::list_cameras(std::vector<vms::cfg::CameraSetup>& result)
{
  if (m_server_id.empty())
  {
    logger::instance().error("APP_ID env var must be set before requesting video_srv cameras");
    return false;
  }

  vms::cfg::ListCamerasRequest request;
  vms::cfg::ListCamerasResponse response;
  request.set_server_id(m_server_id);

  grpc::ClientContext context;

  // optional: a timeout
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
    result.push_back(cam);
  }

  return true;
}

bool cfg_client::get_own_node(std::string& fs_id, std::string& onvif_id)
{
  vms::cfg::ListMediaServersRequest request;
  vms::cfg::ListMediaServersResponse response;

  grpc::ClientContext context;
  context.set_deadline(std::chrono::system_clock::now() + std::chrono::seconds(5));
  context.AddMetadata("dapr-app-id", m_app_id);

  grpc::Status status = m_stub->ListMediaServers(&context, request, &response);

  if (!status.ok())
  {
    logger::instance().error(std::format(
      "ListMediaServers RPC via Dapr failed for app '{}': {}",
      m_app_id, status.error_message()));
    return false;
  }

  for (const auto& server : response.servers())
  {
    if (server.server_id() == m_server_id)
    {
      if (server.fs_id().empty() || server.onvif_id().empty())
      {
        logger::instance().error(std::format(
          "Server '{}' has no {} configured in MEDIA_SERVER_PAIRS", m_server_id,
          server.fs_id().empty() ? "fs_id" : "onvif_id"));
        return false;
      }
      fs_id = server.fs_id();
      onvif_id = server.onvif_id();
      return true;
    }
  }

  logger::instance().error(std::format(
    "Server '{}' not found in ListMediaServers response — check MEDIA_SERVER_PAIRS in VmsCfg",
    m_server_id));
  return false;
}

bool cfg_client::list_media_mtx_servers(std::vector<vms::cfg::MediaMtxServer>& result)
{
  vms::cfg::ListMediaMtxServersRequest request;
  vms::cfg::ListMediaMtxServersResponse response;

  grpc::ClientContext context;
  context.set_deadline(std::chrono::system_clock::now() + std::chrono::seconds(2));
  context.AddMetadata("dapr-app-id", m_app_id);

  grpc::Status status = m_stub->ListMediaMtxServers(&context, request, &response);

  if (!status.ok())
  {
    logger::instance().error(std::format(
      "ListMediaMtxServers RPC via Dapr failed for app '{}': {}",
      m_app_id,
      status.error_message()));
    return false;
  }

  result.reserve(response.servers_size());
  for (const auto& s : response.servers())
    result.push_back(s);

  return true;
}
