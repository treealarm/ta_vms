#pragma once

#include <grpcpp/grpcpp.h>

#include "logger.grpc.pb.h"

class log_service final : public vms::logger::LogService::Service
{
public:
  grpc::Status GetLogs(grpc::ServerContext* context,
    const vms::logger::LogRequest* request,
    vms::logger::LogResponse* response) override;
};
