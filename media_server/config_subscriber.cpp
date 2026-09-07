#include "config_subscriber.h"

#include "grpc_layer/fs_client.h"
#include "interfaces/web_rtc_utils.h"
#include "vms_cfg.pb.h"

#include <format>
#include <unordered_set>

#include "grpc_layer/proto_mapper.h"
#include "logger/logger.h"

namespace {

// Reads the value of a top-level string field out of the event payload. media_server has no JSON
// parser and needs exactly one field out of a message this repository also writes, so pulling in a
// library (and rebuilding the ta-deps base image shared with video_a) for it is not worth it.
// Anything unexpected yields an empty string, which the caller treats as "not for us".
std::string read_json_string_field(const std::string& json, const std::string& field)
{
  const std::string key = "\"" + field + "\"";
  const auto key_pos = json.find(key);
  if (key_pos == std::string::npos)
    return {};

  auto pos = json.find(':', key_pos + key.size());
  if (pos == std::string::npos)
    return {};

  pos = json.find('"', pos + 1);
  if (pos == std::string::npos)
    return {};

  std::string value;
  for (++pos; pos < json.size(); ++pos)
  {
    if (json[pos] == '\\' && pos + 1 < json.size())
    {
      value += json[++pos];
      continue;
    }
    if (json[pos] == '"')
      return value;
    value += json[pos];
  }
  return {};
}

}  // namespace

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

  auto* storage_sub = response->add_subscriptions();
  storage_sub->set_pubsub_name(m_pubsub_name);
  storage_sub->set_topic(k_storage_topic);
  return grpc::Status::OK;
}

grpc::Status config_subscriber::OnTopicEvent(
  grpc::ServerContext* /*context*/,
  const dapr::proto::runtime::v1::TopicEventRequest* request,
  dapr::proto::runtime::v1::TopicEventResponse* response)
{
  if (request->topic() == k_storage_topic)
  {
    // Nothing to filter on and nothing to reload: the storage answers are simply no longer
    // trustworthy, and the next segment will ask again.
    logger::instance().info(
      "config_subscriber: storage configuration changed, dropping cached storage assignments");
    fs_client::instance().invalidate_storage_cache();
    response->set_status(dapr::proto::runtime::v1::TopicEventResponse::SUCCESS);
    return grpc::Status::OK;
  }

  // Payload names the server whose configuration changed — only react if it's ours. Dapr unwraps
  // the CloudEvent envelope, so the data field holds the object VmsCfg published:
  // {"server_id":"vms","camera_ids":["cam1"]}.
  const std::string server_id = read_json_string_field(request->data(), "server_id");

  if (server_id.empty() || server_id != m_cfg.server_id())
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

  std::unordered_map<std::string, webrtc_publish_config> mtx_publish_by_id;
  for (const auto& s : mtx_servers)
  {
    const auto base = "rtsp://" + s.host() + ":" + std::to_string(s.rtsp_port());
    mtx_publish_by_id[s.id()] = webrtc_publish_config{
      .rtsp_push_url = webrtc_rtsp_detail::with_userinfo(base, s.publish_user(), s.publish_pass()),
      .transcode_hevc = s.transcode_hevc(),
    };
  }

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

    webrtc_publish_config publish;
    if (c.has_media_mtx_server_id() && !c.media_mtx_server_id().empty())
    {
      auto it = mtx_publish_by_id.find(c.media_mtx_server_id());
      if (it != mtx_publish_by_id.end())
        publish = it->second;
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
        .policy               = proto_mapper::recording_policy_for_stream(s, c.schedule_active()),
        .web_rtc              = s.web_rtc(),
        .webrtc              = publish,
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
