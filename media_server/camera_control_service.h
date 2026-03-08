#pragma once

#include <grpcpp/grpcpp.h>

#include "camera_manager.h"
#include "vms_control.grpc.pb.h"

class camera_control_service final : public vms::control::CameraControlService::Service {
public:
  explicit camera_control_service(camera_manager& manager);

  grpc::Status AddCamera(grpc::ServerContext* context,
                         const vms::control::AddCameraRequest* request,
                         vms::control::OperationStatus* response) override;

  grpc::Status ListCameras(grpc::ServerContext* context,
                           const vms::control::ListCamerasRequest* request,
                           vms::control::ListCamerasResponse* response) override;

  grpc::Status StartRecording(grpc::ServerContext* context,
                              const vms::control::CameraRequest* request,
                              vms::control::OperationStatus* response) override;

  grpc::Status StopRecording(grpc::ServerContext* context,
                             const vms::control::CameraRequest* request,
                             vms::control::OperationStatus* response) override;

  grpc::Status StartPlayFile(grpc::ServerContext* context,
                             const vms::control::CameraRequest* request,
                             vms::control::OperationStatus* response) override;

  grpc::Status StopPlayFile(grpc::ServerContext* context,
                            const vms::control::CameraRequest* request,
                            vms::control::OperationStatus* response) override;

  grpc::Status StartJpegStream(grpc::ServerContext* context,
                               const vms::control::JpegStreamRequest* request,
                               vms::control::OperationStatus* response) override;

  grpc::Status StopJpegStream(grpc::ServerContext* context,
                              const vms::control::CameraRequest* request,
                              vms::control::OperationStatus* response) override;

  grpc::Status StreamJpegFrames(grpc::ServerContext* context,
                                const vms::control::CameraRequest* request,
                                grpc::ServerWriter<vms::control::JpegFrame>* writer) override;

private:
  camera_manager& m_manager;
};
