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
#include "grpc_layer/entity_state_publisher.h"
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
  logger::instance().init(app_id ? app_id : "vms", log_level::debug);

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

  entity_state_publisher entity_pub;
  std::thread entity_state_thread([&]() {
    while (!g_stop_requested) {
      for (const auto& s : manager.list_stream_states())
        entity_pub.publish_stream_state(s);
      for (int i = 0; i < 10 && !g_stop_requested; ++i)
        std::this_thread::sleep_for(std::chrono::seconds(1));
    }
  });

  // Wait until VmsCfg is reachable before proceeding.
  {
    std::vector<vms::cfg::CameraSetup> warmup_list;
    while (!c_client.list_cameras(warmup_list) && !g_stop_requested)
      std::this_thread::sleep_for(std::chrono::milliseconds(5000));
  }

  {
    std::string fs_id;
    while (!c_client.get_paired_fs_id(fs_id) && !g_stop_requested)
      std::this_thread::sleep_for(std::chrono::milliseconds(5000));
    fs_client::set_fs_app_id(fs_id);
  }

  // Load the initial camera set through the same path pub/sub updates use, so disabled
  // cameras/streams are respected here too instead of being configured then immediately
  // left running until the next unrelated config change.
  cfg_sub.reload_cameras();

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
  entity_state_thread.join();
  return 0;
}
