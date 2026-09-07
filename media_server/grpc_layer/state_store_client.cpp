#include "state_store_client.h"

#include <cstdlib>
#include <chrono>
#include <format>
#include <stdexcept>

#include <grpcpp/grpcpp.h>

extern "C" {
#include <libavcodec/avcodec.h>
}

#include "dapr/proto/common/v1/common.pb.h"
#include "dapr/proto/runtime/v1/dapr.grpc.pb.h"
#include "logger/logger.h"

namespace {
std::string require_env(const char* key)
{
  const char* value = std::getenv(key);
  if (!value || *value == '\0')
    throw std::runtime_error(std::format("Required environment variable '{}' is not set", key));
  return value;
}
}

state_store_client::state_store_client()
  : m_server_id(require_env("APP_ID")),
    m_store_name(require_env("DAPR_STATE_STORE_NAME")),
    m_channel(grpc::CreateChannel("127.0.0.1:" + require_env("DAPR_GRPC_PORT"), grpc::InsecureChannelCredentials())),
    m_stub(dapr::proto::runtime::v1::Dapr::NewStub(m_channel))
{
}

namespace {
/// Codec as a name rather than a number: the consumer is a browser, and an AVCodecID is an ffmpeg
/// build detail nobody outside this process should have to decode.
const char* codec_name_of(AVCodecID id)
{
  const char* name = avcodec_get_name(id);
  return name ? name : "unknown";
}
}

void state_store_client::save_stream_stats(
  const std::vector<camera_manager::stream_state>& streams, int ttl_seconds)
{
  dapr::proto::runtime::v1::SaveStateRequest request;
  request.set_store_name(m_store_name);

  for (const auto& s : streams)
  {
    // Nothing observed yet — publishing zeroes would look like a real measurement of a 0x0 stream
    // and would make the monitor prefer it.
    if (s.video.width <= 0 || s.video.height <= 0)
      continue;

    auto* item = request.add_states();
    item->set_key(std::format("stream-stats-{}/{}", s.camera_id, s.stream_name));

    const int64_t observed_at_ms = s.video.observed_at
      ? std::chrono::duration_cast<std::chrono::milliseconds>(
          s.video.observed_at->time_since_epoch()).count()
      : 0;

    // Hand-built JSON: media_server has no JSON library (see the note in config_subscriber.cpp).
    // An object, unlike the bare scalar save_heartbeat writes, needs no quoting games on the .NET
    // side — GetStateAsync<T> deserializes it directly.
    item->set_value(std::format(
      R"({{"width":{},"height":{},"codec":"{}","fps":{:.3f},"bitrateBps":{},"observedAtMs":{},)"
      R"("transcodeEncoder":"{}"}})",
      s.video.width,
      s.video.height,
      codec_name_of(s.video.codec_id),
      s.video.fps,
      s.video.bitrate_bps,
      observed_at_ms,
      s.video_encoder));

    (*item->mutable_metadata())["ttlInSeconds"] = std::to_string(ttl_seconds);
  }

  if (request.states_size() == 0)
    return;

  google::protobuf::Empty response;
  grpc::ClientContext context;
  context.set_deadline(std::chrono::system_clock::now() + std::chrono::seconds(5));

  grpc::Status status = m_stub->SaveState(&context, request, &response);

  if (!status.ok())
  {
    logger::instance().warning(std::format(
      "state_store stream stats failed for server '{}': {}",
      m_server_id,
      status.error_message()));
  }
}

void state_store_client::save_heartbeat()
{
  dapr::proto::runtime::v1::SaveStateRequest request;
  request.set_store_name(m_store_name);

  auto* item = request.add_states();
  item->set_key("ms-status-" + m_server_id);
  // JSON-quoted on purpose: the reader is VmsCfg's GetStateAsync<string>, and the Dapr .NET SDK
  // always runs the value through its JSON serializer. A bare `online` fails to deserialize there,
  // which used to surface as "this server is offline" instead of as an error.
  item->set_value("\"online\"");
  (*item->mutable_metadata())["ttlInSeconds"] = "60";

  google::protobuf::Empty response;
  grpc::ClientContext context;
  context.set_deadline(std::chrono::system_clock::now() + std::chrono::seconds(5));

  grpc::Status status = m_stub->SaveState(&context, request, &response);

  if (!status.ok())
  {
    logger::instance().warning(std::format(
      "state_store heartbeat failed for server '{}': {}",
      m_server_id,
      status.error_message()));
  }
}
