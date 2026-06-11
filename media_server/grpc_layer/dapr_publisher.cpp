#include "dapr_publisher.h"

#include <cstdlib>
#include <format>
#include <stdexcept>

#include <grpcpp/grpcpp.h>

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

dapr_publisher::dapr_publisher()
  : m_pubsub_name(require_env("PUBSUB_NAME")),
    m_channel(grpc::CreateChannel("127.0.0.1:50001", grpc::InsecureChannelCredentials())),
    m_stub(dapr::proto::runtime::v1::Dapr::NewStub(m_channel))
{
}

bool dapr_publisher::publish_json(const std::string& topic, const std::string& payload) const
{
  dapr::proto::runtime::v1::PublishEventRequest request;
  request.set_pubsub_name(m_pubsub_name);
  request.set_topic(topic);
  request.set_data(payload);
  request.set_data_content_type("application/json");

  google::protobuf::Empty response;
  grpc::ClientContext context;
  grpc::Status status = m_stub->PublishEvent(&context, request, &response);

  if (!status.ok()) {
    logger::instance().warning(std::format(
      "dapr publish failed, topic='{}', error='{}'", topic, status.error_message()));
    return false;
  }

  return true;
}
