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
                                            request->start_immediately(),
                                            error);

  response->set_success(success);
  response->set_message(success ? "camera added" : error);
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

grpc::Status camera_control_service::StopRecording(
    grpc::ServerContext* /*context*/,
    const vms::control::CameraRequest* request,
    vms::control::OperationStatus* response) {
  std::string error;
  const bool success = m_manager.stop_recording(request->camera_id(), error);

  response->set_success(success);
  response->set_message(success ? "recording stopped" : error);
  return grpc::Status::OK;
}
