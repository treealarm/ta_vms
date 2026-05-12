#include <csignal>
#include <atomic>
#include <chrono>
#include <cstdlib>
#include <memory>
#include <string>
#include <thread>

#include <grpcpp/grpcpp.h>

#include "camera_manager.h"
#include "grpc_layer/camera_control_service.h"
#include "grpc_layer/cfg_client.h"
#include "grpc_layer/proto_mapper.h"

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
  std::string server_address = "0.0.0.0:50051";
  if (argc > 1)
  {
    server_address = argv[1];
  } 
  else if (const char* grpc_vms_port = std::getenv("GRPC_VMS_PORT"))
  {
    if (*grpc_vms_port != '\0') 
    {
      server_address = "0.0.0.0:" + std::string(grpc_vms_port);
    }
  }

  logger::instance().init();

  std::signal(SIGINT, on_signal);
  std::signal(SIGTERM, on_signal);

  camera_manager manager;
  camera_control_service service(manager);

  grpc::ServerBuilder builder;
  builder.AddListeningPort(server_address, grpc::InsecureServerCredentials());
  builder.RegisterService(&service);

  std::unique_ptr<grpc::Server> server(builder.BuildAndStart());
  if (!server) 
  {
    logger::instance().error(std::format("Failed to start gRPC server on {}", server_address));
    return 1;
  }

  logger::instance().info(std::format("gRPC server is listening on  {}", server_address));

  cfg_client c_client;
  std::vector<vms::cfg::CameraSetup> cfg_list;

  while (!c_client.register_media_server() && !g_stop_requested)
  {
    std::this_thread::sleep_for(std::chrono::milliseconds(5000));
  }
  
  while (!c_client.list_cameras(cfg_list) && !g_stop_requested)
  {
    std::this_thread::sleep_for(std::chrono::milliseconds(5000));
  }

  std::string error;

  for (const auto& c : cfg_list)
  {
    const std::string user = c.has_cred() ? c.cred().user() : "";
    const std::string pass = c.has_cred() ? c.cred().pass() : "";

    std::vector<camera_manager::stream_config> streams;
    for (int i = 0; i < c.streams_size(); ++i)
    {
      const auto& s = c.streams(i);
      streams.push_back({
        .stream_name   = s.stream_name(),
        .profile_token = s.profile_token(),
        .rtsp_url      = s.rtsp_url(),
        .policy        = proto_mapper::recording_policy_for_stream(s),
        .web_rtc       = s.web_rtc()
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

  while (!g_stop_requested) {
    std::this_thread::sleep_for(std::chrono::milliseconds(200));
  }

  manager.stop_all();
  server->Shutdown();
  server->Wait();
  return 0;
}
