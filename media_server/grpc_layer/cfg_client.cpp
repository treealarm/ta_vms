#include "cfg_client.h"

#include <cstdlib>
#include <iostream>

#include <grpcpp/grpcpp.h>


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

cfg_client::cfg_client()
  :
    m_channel(grpc::CreateChannel(
      read_env_or_default("CFG_VMS_ADDR", "127.0.0.1:5135"),
      grpc::InsecureChannelCredentials())),
    m_stub(vms::cfg::CfgService::NewStub(m_channel))
{
}

