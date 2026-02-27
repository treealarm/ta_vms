#include "dapr_publisher.h"

#include <cstdlib>
#include <iostream>

#include <grpcpp/grpcpp.h>

#include "dapr_runtime.grpc.pb.h"

namespace {
std::string read_env_or_default(const char* key, const char* fallback)
{
  const char* value = std::getenv(key);
  if (!value || *value == '\0') {
    return fallback;
  }

  return value;
}
}

dapr_publisher::dapr_publisher()
  : m_pubsub_name(read_env_or_default("DAPR_PUBSUB_NAME", "pubsub")),
    m_channel(grpc::CreateChannel(
      read_env_or_default("DAPR_GRPC_ENDPOINT", "127.0.0.1:50001"),
      grpc::InsecureChannelCredentials())),
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

  dapr::proto::runtime::v1::PublishEventResponse response;
  grpc::ClientContext context;
  grpc::Status status = m_stub->PublishEvent(&context, request, &response);

  if (!status.ok()) {
    std::cerr << "dapr publish failed, topic='" << topic
              << "', error='" << status.error_message() << "'\n";
    return false;
  }

  return true;
}
