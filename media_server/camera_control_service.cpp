#include "camera_control_service.h"

camera_control_service::camera_control_service(camera_manager& manager)
    : m_manager(manager) {}

grpc::Status camera_control_service::AddCamera(
    grpc::ServerContext* /*context*/,
    const vms::control::AddCameraRequest* request,
    vms::control::OperationStatus* response) {
  std::string error;
  const bool success = m_manager.add_camera(request->camera_id(),
                                            request->rtsp_url(),
                                            request->storage_base_path(),
                                            request->state().recording(),
                                            request->state().jpeg_stream(),
                                            error);

  response->set_success(success);
  response->set_message(success ? "camera added" : error);
  return grpc::Status::OK;
}

grpc::Status camera_control_service::ListCameras(
    grpc::ServerContext* /*context*/,
    const vms::control::ListCamerasRequest* /*request*/,
    vms::control::ListCamerasResponse* response) {
  for (const auto& camera : m_manager.list_cameras()) {
    auto* item = response->add_cameras();
    item->set_camera_id(camera.camera_id);
    item->set_rtsp_url(camera.rtsp_url);
    item->set_is_recording(camera.is_recording);
  }

  return grpc::Status::OK;
}

grpc::Status camera_control_service::StartRecording(
    grpc::ServerContext* /*context*/,
    const vms::control::CameraRequest* request,
    vms::control::OperationStatus* response) {
  std::string error;
  const bool success = m_manager.start_recording(request->camera_id(), error);

  response->set_success(success);
  response->set_message(success ? "recording started" : error);
  return grpc::Status::OK;
}

grpc::Status camera_control_service::SetCameraState(
    grpc::ServerContext* /*context*/,
    const vms::control::SetCameraStateRequest* request,
    vms::control::OperationStatus* response) {
  std::string error;
  const bool success = m_manager.set_camera_state(request->camera_id(),
                                                  request->state().recording(),
                                                  request->state().jpeg_stream(),
                                                  error);

  response->set_success(success);
  response->set_message(success ? "camera state updated" : error);
  return grpc::Status::OK;
}

grpc::Status camera_control_service::StartPlayFile(
    grpc::ServerContext* /*context*/,
    const vms::control::PlayFileRequest* request,
    vms::control::OperationStatus* response) {
  std::string error;
  const bool success = m_manager.start_play_file(request->camera_id(),
                                                 request->start_time(),
                                                 error);

  response->set_success(success);
  response->set_message(success ? "file playback started" : error);
  return grpc::Status::OK;
}

grpc::Status camera_control_service::StopPlayFile(
    grpc::ServerContext* /*context*/,
    const vms::control::CameraRequest* request,
    vms::control::OperationStatus* response) {
  std::string error;
  const bool success = m_manager.stop_play_file(request->camera_id(),
                                                error);

  response->set_success(success);
  response->set_message(success ? "file playback stopped" : error);
  return grpc::Status::OK;
}

