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

  grpc::Status AddArchPlayer(grpc::ServerContext* context,
                             const vms::control::PlayFileRequest* request,
                             vms::control::OperationStatus* response) override;

  grpc::Status ListCameras(grpc::ServerContext* context,
                           const vms::control::ListCamerasRequest* request,
                           vms::control::ListCamerasResponse* response) override;


  grpc::Status SetCameraState(grpc::ServerContext* context,
                              const vms::control::SetCameraStateRequest* request,
                              vms::control::OperationStatus* response) override;

  grpc::Status SetPlayerState(grpc::ServerContext* context,
                              const vms::control::PlayFileRequest* request,
                              vms::control::OperationStatus* response) override;

  grpc::Status StreamJpegFrames(grpc::ServerContext* context,
                                const vms::control::CameraRequest* request,
                                grpc::ServerWriter<vms::control::JpegFrame>* writer) override;

private:
  camera_manager& m_manager;
};
