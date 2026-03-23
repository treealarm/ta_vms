#pragma once

#include <grpcpp/grpcpp.h>

#include "file_system/recording_storage.h"
#include "camera_manager.h"
#include "vms_control.grpc.pb.h"

class camera_control_service final : public vms::control::CameraControlService::Service 
{
public:
  explicit camera_control_service(camera_manager& manager);

  grpc::Status AddCamera(grpc::ServerContext* context,
                         const vms::control::CameraState* request,
                         vms::control::OperationStatus* response) override;

  grpc::Status AddArchPlayer(grpc::ServerContext* context,
                             const vms::control::PlayerState* request,
                             vms::control::OperationStatus* response) override;

  grpc::Status RemoveCamera(grpc::ServerContext* context,
                            const vms::control::SourceRequest* request,
                            vms::control::OperationStatus* response) override;

  grpc::Status RemoveArchPlayer(grpc::ServerContext* context,
                                const vms::control::SourceRequest* request,
                                vms::control::OperationStatus* response) override;

  grpc::Status ListCameras(grpc::ServerContext* context,
                           const vms::control::ListCamerasRequest* request,
                           vms::control::ListCamerasResponse* response) override;


  grpc::Status SetCameraState(grpc::ServerContext* context,
                              const vms::control::CameraState* request,
                              vms::control::OperationStatus* response) override;

  grpc::Status GetCameraState(grpc::ServerContext* context,
    const vms::control::SourceRequest* request,
    vms::control::CameraState* response) override;

  grpc::Status SetPlayerState(grpc::ServerContext* context,
                              const vms::control::PlayerState* request,
                              vms::control::OperationStatus* response) override;

  grpc::Status GetPlayerState(grpc::ServerContext* context,
    const vms::control::SourceRequest* request,
    vms::control::PlayerState* response);

  grpc::Status ListPlayers(grpc::ServerContext* context,
    const vms::control::ListPlayersRequest* request,
    vms::control::ListPlayersResponse* response) override;

  grpc::Status GetRecordings(grpc::ServerContext* context,
    const vms::control::RecordingsRequest* request,
    vms::control::RecordingsResponse* response) override;



private:
  camera_manager& m_manager;
  recording_storage m_storage;
};
