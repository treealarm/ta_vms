#include <csignal>
#include <algorithm>
#include <map>
#include <utility>
#include <vector>
#include <atomic>
#include <chrono>
#include <cstdlib>
#include <format>
#include <stdexcept>
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

#ifdef VMS_WITH_TA_ROIENC
#include "writer/processors/hw_transcode_processor.h"
#endif

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
  //
  // VMS_VIDEO_ENCODER is checked in the same breath and for the same reason: it is only read
  // when a camera publishes something other than H.264, so a misspelled backend would otherwise
  // surface as one stream that will not play, on whichever host happens to have such a camera.
  try
  {
#ifdef VMS_WITH_TA_ROIENC
    hw_transcode_processor::validate_configuration();
#else
    // Built without the roitrc/ta_roienc submodule: video_transcode_processor is the only video
    // encoder there is. Naming one anyway asks for something this binary cannot do, and quietly
    // getting libx264 instead is the failure the variable exists to make visible.
    if (const char* encoder = std::getenv("VMS_VIDEO_ENCODER"); encoder && *encoder != '\0')
    {
      throw std::invalid_argument(std::format(
        "VMS_VIDEO_ENCODER={}: this build has no ta_roienc, so the CPU encoder is the only one; "
        "unset the variable or build with the roitrc/ta_roienc submodule", encoder));
    }
    logger::instance().warning("built without ta_roienc: HEVC streams are re-encoded for WebRTC on the CPU");
#endif
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

  // Observed stream parameters for the monitor's adaptive quality choice. Published from the
  // liveness thread rather than a timer of its own: that loop already walks every stream once a
  // second, and a second thread doing the same walk would buy nothing.
  //
  // The TTL is at least three publish periods, so a single lost write never makes a live stream
  // look stale, and expiry is what marks a snapshot as no longer trustworthy — there is no age
  // check anywhere downstream.
  const int stats_period = std::max(1, env_int("VMS_STREAM_STATS_PERIOD_SECONDS", 15));
  const int stats_ttl = std::max(45, stats_period * 3);
  auto publish_stream_stats = [&state_client, stats_ttl](
    const std::vector<camera_manager::stream_state>& streams) {
    state_client.save_stream_stats(streams, stats_ttl);
  };

  std::thread liveness_thread([&, liveness_period, stats_period]() {
    // What was last published, so a change in the encoder's output goes out at once instead of
    // waiting for the next full refresh: a stream that switches resolution must not leave the
    // monitor choosing by a measurement that no longer describes it.
    std::map<std::string, std::pair<int, int>> published_size;
    int seconds_since_full_stats = stats_period;

    while (!g_stop_requested) {
      // A dropped source must not leave its clip spanning the outage: the archive would claim
      // footage it does not have, and the gap would never be detected.
      manager.close_segments_on_source_loss();
      send_liveness();

      for (auto i = std::chrono::seconds::zero(); i < liveness_period && !g_stop_requested;
           i += std::chrono::seconds(1))
      {
        const auto states = manager.list_stream_states();

        std::vector<camera_manager::stream_state> changed;
        for (const auto& st : states)
        {
          const auto key = st.camera_id + "/" + st.stream_name;
          const auto size = std::pair{ st.video.width, st.video.height };
          auto it = published_size.find(key);
          if (it == published_size.end() || it->second != size)
          {
            published_size[key] = size;
            changed.push_back(st);
          }
        }

        if (++seconds_since_full_stats >= stats_period)
        {
          publish_stream_stats(states);
          seconds_since_full_stats = 0;
        }
        else if (!changed.empty())
        {
          publish_stream_stats(changed);
        }

        std::this_thread::sleep_for(std::chrono::seconds(1));
      }
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
