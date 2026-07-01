#include "config_subscriber.h"

#include <format>
#include <unordered_set>

#include "grpc_layer/proto_mapper.h"
#include "logger/logger.h"

config_subscriber::config_subscriber(
  camera_manager& cameras, cfg_client& cfg, std::string pubsub_name)
  : m_cameras(cameras)
  , m_cfg(cfg)
  , m_pubsub_name(std::move(pubsub_name))
{}

grpc::Status config_subscriber::ListTopicSubscriptions(
  grpc::ServerContext* /*context*/,
  const google::protobuf::Empty* /*request*/,
  dapr::proto::runtime::v1::ListTopicSubscriptionsResponse* response)
{
  auto* sub = response->add_subscriptions();
  sub->set_pubsub_name(m_pubsub_name);
  sub->set_topic(k_topic);
  return grpc::Status::OK;
}

grpc::Status config_subscriber::OnTopicEvent(
  grpc::ServerContext* /*context*/,
  const dapr::proto::runtime::v1::TopicEventRequest* request,
  dapr::proto::runtime::v1::TopicEventResponse* response)
{
  // Payload is the server_id that changed — only react if it's ours.
  // Dapr wraps the JSON string payload in a CloudEvent envelope; the raw data field
  // contains the JSON-encoded string e.g. "\"vms\"".
  const std::string& data = request->data();

  // Strip JSON string quotes if present
  std::string server_id = data;
  if (server_id.size() >= 2 && server_id.front() == '"' && server_id.back() == '"')
    server_id = server_id.substr(1, server_id.size() - 2);

  if (server_id != m_cfg.server_id())
  {
    response->set_status(dapr::proto::runtime::v1::TopicEventResponse::SUCCESS);
    return grpc::Status::OK;
  }

  logger::instance().info(std::format(
    "config_subscriber: received config change for server '{}', reloading cameras", server_id));

  reload_cameras();

  response->set_status(dapr::proto::runtime::v1::TopicEventResponse::SUCCESS);
  return grpc::Status::OK;
}

void config_subscriber::reload_cameras()
{
  std::vector<vms::cfg::CameraSetup> cfg_list;
  if (!m_cfg.list_cameras(cfg_list))
  {
    logger::instance().warning("config_subscriber: failed to list cameras from VmsCfg");
    return;
  }

  std::vector<vms::cfg::MediaMtxServer> mtx_servers;
  m_cfg.list_media_mtx_servers(mtx_servers);

  std::unordered_map<std::string, std::string> mtx_rtsp_by_id;
  for (const auto& s : mtx_servers)
    mtx_rtsp_by_id[s.id()] = "rtsp://" + s.host() + ":" + std::to_string(s.rtsp_port());

  // Build set of camera IDs that should be running on this server
  std::unordered_set<std::string> expected_ids;
  std::string error;

  for (const auto& c : cfg_list)
  {
    if (c.has_disabled() && c.disabled())
      continue;

    expected_ids.insert(c.camera_id());

    const std::string user = c.has_cred() ? c.cred().user() : "";
    const std::string pass = c.has_cred() ? c.cred().pass() : "";

    std::string push_url;
    if (c.has_media_mtx_server_id() && !c.media_mtx_server_id().empty())
    {
      auto it = mtx_rtsp_by_id.find(c.media_mtx_server_id());
      if (it != mtx_rtsp_by_id.end())
        push_url = it->second;
    }

    std::vector<camera_manager::stream_config> streams;
    for (int i = 0; i < c.streams_size(); ++i)
    {
      const auto& s = c.streams(i);
      if (s.has_disabled() && s.disabled())
        continue;
      streams.push_back({
        .stream_name          = s.stream_name(),
        .profile_token        = s.profile_token(),
        .rtsp_url             = proto_mapper::resolve_stream_path(s, c),
        .policy               = proto_mapper::recording_policy_for_stream(s),
        .web_rtc              = s.web_rtc(),
        .webrtc_rtsp_push_url = push_url,
      });
    }

    if (!m_cameras.configure_camera(c.camera_id(), user, pass, streams, error))
      logger::instance().warning(std::format(
        "config_subscriber: failed to configure camera '{}': {}", c.camera_id(), error));
  }

  // Remove cameras that are no longer assigned to this server
  for (const auto& camera_info : m_cameras.list_cameras())
  {
    if (!expected_ids.count(camera_info.camera_id))
    {
      logger::instance().info(std::format(
        "config_subscriber: removing camera '{}' (no longer on this server)", camera_info.camera_id));
      m_cameras.remove_camera(camera_info.camera_id);
    }
  }
}
