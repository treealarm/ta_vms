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

  grpc::Status GetArchiveFrameJpeg(grpc::ServerContext* context,
    const vms::control::ArchiveFrameJpegRequest* request,
    vms::control::ArchiveFrameJpegResponse* response) override;

private:
  camera_manager& m_manager;
};
