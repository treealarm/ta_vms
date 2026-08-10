#include <csignal>
#include <algorithm>
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
#include "grpc_layer/onvif_metadata_client.h"
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

  // Numeric env reader for optional tuning knobs. Required variables use require_env instead — a
  // missing one there is a misconfiguration and must fail loudly rather than pick a default.
  int env_int(const char* key, int fallback)
  {
    const char* raw = std::getenv(key);
    if (!raw || *raw == '\0') return fallback;
    try { return std::stoi(raw); } catch (...) { return fallback; }
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

  // Started here rather than on the first metadata packet, so a broken environment shows up at
  // startup instead of hours later on the first camera that speaks metadata. It needs nothing
  // onvif_client does not already require.
  try
  {
    onvif_metadata_client::instance().start();
  }
  catch (const std::exception& ex)
  {
    logger::instance().error(ex.what());
    return 1;
  }

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

  // Optional debug feed: one message per stream per period, so its volume is the stream count
  // divided by the period, on a bus shared with the rest of the system. Off unless
  // VMS_DEBUG_ENTITY_STATE_SECONDS names a positive period — the "Entity State" tab is a debugging
  // aid, not something a deployment should pay for by default.
  const int entity_state_period = env_int("VMS_DEBUG_ENTITY_STATE_SECONDS", 0);
  entity_state_publisher entity_pub;
  std::thread entity_state_thread;
  if (entity_state_period > 0)
  {
    logger::instance().info(std::format(
      "debug entity-state publishing every {}s", entity_state_period));
    entity_state_thread = std::thread([&, entity_state_period]() {
      while (!g_stop_requested) {
        for (const auto& s : manager.list_stream_states())
          entity_pub.publish_stream_state(s);
        for (int i = 0; i < entity_state_period && !g_stop_requested; ++i)
          std::this_thread::sleep_for(std::chrono::seconds(1));
      }
    });
  }

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

  // Source liveness. Started only here, after set_fs_app_id above — fs_client's constructor throws
  // if used before that. Deliberately a separate timer rather than the reader threads: the RPC has
  // a deadline and would stall an RTSP read loop.
  const auto liveness_period = std::chrono::seconds(
    std::max(1, env_int("VMS_SOURCE_LIVENESS_PERIOD_SECONDS", 15)));
  const std::string own_server_id = app_id ? app_id : "";
  // Identifies this run to VmsFs. A process that dies and comes back inside one heartbeat period
  // never looks silent, so the archive's hole would go unrecorded; a changed id is what says
  // "everything between the last frame you saw and the first one now is missing".
  const std::string instance_id = std::format("{}-{}", own_server_id,
    std::chrono::duration_cast<std::chrono::milliseconds>(
      std::chrono::system_clock::now().time_since_epoch()).count());
  auto send_liveness = [&manager, own_server_id, instance_id]() {
    std::vector<fs_client::liveness_entry> batch;
    for (const auto& s : manager.list_stream_states())
    {
      batch.push_back(fs_client::liveness_entry{
        .camera_id = s.camera_id,
        .stream_name = s.stream_name,
        .alive = s.source_alive,
        .last_frame_ts = s.last_frame_ts,
        .recovered_frame_ts = s.recovered_frame_ts,
        .recording_expected = s.is_recording,
      });
    }
    fs_client::instance().report_source_liveness(own_server_id, instance_id, batch);
  };

  std::thread liveness_thread([&, liveness_period]() {
    while (!g_stop_requested) {
      // A dropped source must not leave its clip spanning the outage: the archive would claim
      // footage it does not have, and the gap would never be detected.
      manager.close_segments_on_source_loss();
      send_liveness();

      for (auto i = std::chrono::seconds::zero(); i < liveness_period && !g_stop_requested;
           i += std::chrono::seconds(1))
        std::this_thread::sleep_for(std::chrono::seconds(1));
    }

    // One last report on the way out. Without it the newest frame VmsFs knows about is up to a
    // whole period old, and a restart is recorded as having begun that much earlier than it did —
    // claiming a hole over footage the archive actually has.
    send_liveness();
  });

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
  // After stop_all, so no reader thread is still queueing documents into a sender being torn down.
  onvif_metadata_client::instance().stop();
  server->Shutdown();
  server->Wait();
  heartbeat_thread.join();
  if (liveness_thread.joinable()) liveness_thread.join();
  if (entity_state_thread.joinable()) entity_state_thread.join();
  return 0;
}
