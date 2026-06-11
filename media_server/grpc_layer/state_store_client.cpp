#include "state_store_client.h"

#include <cstdlib>
#include <format>
#include <stdexcept>

#include <grpcpp/grpcpp.h>

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

void state_store_client::save_heartbeat()
{
  dapr::proto::runtime::v1::SaveStateRequest request;
  request.set_store_name(m_store_name);

  auto* item = request.add_states();
  item->set_key("ms-status-" + m_server_id);
  item->set_value("online");
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
