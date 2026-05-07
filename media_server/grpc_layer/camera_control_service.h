#pragma once

#include <grpcpp/grpcpp.h>

#include "file_system/recording_storage.h"
#include "camera_manager.h"
#include "reader/archive_frame_jpeg_reader.h"
#include "vms_control.grpc.pb.h"

class camera_control_service final : public vms::control::CameraControlService::Service 
{
public:
  explicit camera_control_service(camera_manager& manager);

  grpc::Status AddArchPlayer(grpc::ServerContext* context,
                             const vms::control::PlayerState* request,
                             vms::control::OperationStatus* response) override;

  grpc::Status RemoveCamera(grpc::ServerContext* context,
                            const vms::control::SourceRequest* request,
                            vms::control::OperationStatus* response) override;

  grpc::Status RemoveArchPlayer(grpc::ServerContext* context,
                                const vms::control::SourceRequest* request,
                                vms::control::OperationStatus* response) override;

  grpc::Status SetCameraState(grpc::ServerContext* context,
                              const vms::control::CameraState* request,
                              vms::control::OperationStatus* response) override;

  grpc::Status SetPlayerState(grpc::ServerContext* context,
                              const vms::control::PlayerState* request,
                              vms::control::OperationStatus* response) override;

  ::grpc::Status GetPlayerProgressState(
    ::grpc::ServerContext* context, 
    const ::vms::control::SourceRequest* request, 
    ::vms::control::PlayerProgressState* response) override;

  ::grpc::Status SendPlayerCommand(
    ::grpc::ServerContext* context, 
    const ::vms::control::PlayerCommand* request, 
    ::vms::control::OperationStatus* response) override;



  grpc::Status ListPlayers(grpc::ServerContext* context,
    const vms::control::ListPlayersRequest* request,
    vms::control::ListPlayersResponse* response) override;

  grpc::Status GetRecordings(grpc::ServerContext* context,
    const vms::control::RecordingsRequest* request,
    vms::control::RecordingsResponse* response) override;

  grpc::Status GetArchiveFrameJpeg(grpc::ServerContext* context,
    const vms::control::ArchiveFrameJpegRequest* request,
    vms::control::ArchiveFrameJpegResponse* response) override;

private:
  camera_manager& m_manager;
  recording_storage m_storage;
};
