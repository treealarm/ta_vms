#pragma once

#include <grpcpp/grpcpp.h>

#include "camera_manager.h"
#include "reader/archive_frame_jpeg_reader.h"
#include "vms_control.grpc.pb.h"

class camera_control_service final : public vms::control::CameraControlService::Service
{
public:
  explicit camera_control_service(camera_manager& manager);

  grpc::Status RemoveCamera(grpc::ServerContext* context,
                            const vms::control::SourceRequest* request,
                            vms::control::OperationStatus* response) override;

  grpc::Status SetCameraState(grpc::ServerContext* context,
                              const vms::control::CameraState* request,
                              vms::control::OperationStatus* response) override;

  grpc::Status EnsureWebRtcStreaming(grpc::ServerContext* context,
                                     const vms::control::EnsureWebRtcStreamingRequest* request,
                                     vms::control::OperationStatus* response) override;

  grpc::Status EnsureAnalyticsPush(grpc::ServerContext* context,
                                   const vms::control::EnsureAnalyticsPushRequest* request,
                                   vms::control::OperationStatus* response) override;
  grpc::Status StopAnalyticsPush(grpc::ServerContext* context,
                                 const vms::control::AnalyticsPushKey* request,
                                 vms::control::OperationStatus* response) override;

  grpc::Status GetArchiveFrameJpeg(grpc::ServerContext* context,
    const vms::control::ArchiveFrameJpegRequest* request,
    vms::control::ArchiveFrameJpegResponse* response) override;

  grpc::Status StartEdgeImport(grpc::ServerContext* context,
                               const vms::control::EdgeImportRequest* request,
                               vms::control::EdgeImportJob* response) override;

  grpc::Status GetEdgeImportJob(grpc::ServerContext* context,
                                const vms::control::EdgeImportJobKey* request,
                                vms::control::EdgeImportJob* response) override;

  grpc::Status CancelEdgeImport(grpc::ServerContext* context,
                                const vms::control::EdgeImportJobKey* request,
                                vms::control::OperationStatus* response) override;

  grpc::Status ListEdgeImportJobs(grpc::ServerContext* context,
                                  const vms::control::ListEdgeImportJobsRequest* request,
                                  vms::control::ListEdgeImportJobsResponse* response) override;

private:
  camera_manager& m_manager;
};
