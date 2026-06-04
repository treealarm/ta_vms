#include <csignal>
#include <atomic>
#include <chrono>
#include <cstdlib>
#include <memory>
#include <string>
#include <thread>
#include <unordered_map>

#include <grpcpp/grpcpp.h>

#include "camera_manager.h"
#include "config_subscriber.h"
#include "grpc_layer/camera_control_service.h"
#include "grpc_layer/cfg_client.h"
#include "grpc_layer/fs_client.h"
#include "grpc_layer/proto_mapper.h"
#include "grpc_layer/state_store_client.h"
#include "grpc_layer/utils_client.h"
#include "mediamtx_monitor.h"

#include "logger/logger.h"

#include "vms_cfg.pb.h"
#include "vms_control.pb.h"

namespace 
{
  std::atomic<bool> g_stop_requested{false};

  void on_signal(int /*signal*/) 
  {
    g_stop_requested = true;
  }
}  // namespace

int main(int argc, char** argv) 
{
  std::string server_address;
  if (argc > 1)
  {
    server_address = argv[1];
  }
  else
  {
    const char* grpc_vms_port = std::getenv("GRPC_VMS_PORT");
    if (!grpc_vms_port || grpc_vms_port[0] == '\0')
    {
      logger::instance().error("Required environment variable 'GRPC_VMS_PORT' is not set");
      return 1;
    }
    server_address = "0.0.0.0:" + std::string(grpc_vms_port);
  }

  const char* app_id = std::getenv("APP_ID");
  logger::instance().init(app_id ? app_id : "vms");

  std::signal(SIGINT, on_signal);
  std::signal(SIGTERM, on_signal);

  camera_manager manager;
  cfg_client c_client;
  camera_control_service service(manager);
  config_subscriber cfg_sub(manager, c_client,
    std::getenv("PUBSUB_NAME") ? std::getenv("PUBSUB_NAME") : "orderpubsub");

  grpc::ServerBuilder builder;
  builder.AddListeningPort(server_address, grpc::InsecureServerCredentials());
  builder.RegisterService(&service);
  builder.RegisterService(&cfg_sub);

  std::unique_ptr<grpc::Server> server(builder.BuildAndStart());
  if (!server)
  {
    logger::instance().error(std::format("Failed to start gRPC server on {}", server_address));
    return 1;
  }

  logger::instance().info(std::format("gRPC server is listening on  {}", server_address));

  state_store_client state_client;
  std::thread heartbeat_thread([&]() {
    while (!g_stop_requested) {
      state_client.save_heartbeat();
      for (int i = 0; i < 30 && !g_stop_requested; ++i)
        std::this_thread::sleep_for(std::chrono::seconds(1));
    }
  });

  std::vector<vms::cfg::CameraSetup> cfg_list;
  std::vector<vms::cfg::MediaMtxServer> mtx_servers;

  while (!c_client.list_cameras(cfg_list) && !g_stop_requested)
  {
    std::this_thread::sleep_for(std::chrono::milliseconds(5000));
  }

  {
    std::string fs_id;
    while (!c_client.get_paired_fs_id(fs_id) && !g_stop_requested)
      std::this_thread::sleep_for(std::chrono::milliseconds(5000));
    fs_client::set_fs_app_id(fs_id);
  }

  // Best-effort: load MediaMTX server list to resolve RTSP push URLs at startup.
  c_client.list_media_mtx_servers(mtx_servers);

  // Build a lookup map: server_id → rtsp_url
  std::unordered_map<std::string, std::string> mtx_rtsp_by_id;
  for (const auto& s : mtx_servers)
    mtx_rtsp_by_id[s.id()] = s.rtsp_url();

  std::string error;

  for (const auto& c : cfg_list)
  {
    const std::string user = c.has_cred() ? c.cred().user() : "";
    const std::string pass = c.has_cred() ? c.cred().pass() : "";

    // Resolve assigned MediaMTX RTSP push base URL for this camera
    std::string cam_webrtc_push_url;
    if (c.has_media_mtx_server_id() && !c.media_mtx_server_id().empty())
    {
      auto it = mtx_rtsp_by_id.find(c.media_mtx_server_id());
      if (it != mtx_rtsp_by_id.end())
        cam_webrtc_push_url = it->second;
    }

    std::vector<camera_manager::stream_config> streams;
    for (int i = 0; i < c.streams_size(); ++i)
    {
      const auto& s = c.streams(i);
      streams.push_back({
        .stream_name           = s.stream_name(),
        .profile_token         = s.profile_token(),
        .rtsp_url              = s.rtsp_url(),
        .policy                = proto_mapper::recording_policy_for_stream(s),
        .web_rtc               = s.web_rtc(),
        .webrtc_rtsp_push_url  = cam_webrtc_push_url,
      });
    }

    if (!manager.configure_camera(c.camera_id(), user, pass, streams, error))
    {
      logger::instance().error(std::format("Failed to configure camera '{}': {}", c.camera_id(), error));
    }
  }

  //if (argc > 3)
  //{
  //  const auto cam_id = argv[3];
  //  const auto url = argv[2];
  //
  //  const bool success = manager.add_camera(
  //    cam_id,
  //    url,
  //    true,
  //    true,
  //    error);
  //}

  std::unique_ptr<utils_client> u_client;
  std::unique_ptr<mediamtx_monitor> monitor;
  try
  {
    u_client = std::make_unique<utils_client>();
    monitor  = std::make_unique<mediamtx_monitor>(manager, *u_client);
    monitor->start();
    logger::instance().info("mediamtx_monitor started");
  }
  catch (const std::exception& ex)
  {
    logger::instance().warning(std::format("mediamtx_monitor disabled: {}", ex.what()));
  }

  while (!g_stop_requested) {
    std::this_thread::sleep_for(std::chrono::milliseconds(200));
  }

  if (monitor) monitor->stop();
  manager.stop_all();
  server->Shutdown();
  server->Wait();
  heartbeat_thread.join();
  return 0;
}
