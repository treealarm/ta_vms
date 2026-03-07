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
                                            request->start_immediately(),
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

grpc::Status camera_control_service::StartPlayFile(
    grpc::ServerContext* /*context*/,
    const vms::control::PlayFileRequest* request,
    vms::control::OperationStatus* response) {
  std::string error;
  const bool success = m_manager.start_play_file(request->camera_id(),
                                                 request->relative_path(),
                                                 error);

  response->set_success(success);
  response->set_message(success ? "file playback started" : error);
  return grpc::Status::OK;
}

grpc::Status camera_control_service::StopPlayFile(
    grpc::ServerContext* /*context*/,
    const vms::control::PlayFileRequest* request,
    vms::control::OperationStatus* response) {
  std::string error;
  const bool success = m_manager.stop_play_file(request->camera_id(),
                                                request->relative_path(),
                                                error);

  response->set_success(success);
  response->set_message(success ? "file playback stopped" : error);
  return grpc::Status::OK;
}

grpc::Status camera_control_service::StartJpegStream(
    grpc::ServerContext* /*context*/,
    const vms::control::JpegStreamRequest* request,
    vms::control::OperationStatus* response) {
  std::string error;
  const bool success = m_manager.start_jpeg_stream(request->camera_id(), error);

  response->set_success(success);
  response->set_message(success ? "jpeg stream started (grpc)" : error);
  return grpc::Status::OK;
}

grpc::Status camera_control_service::StopJpegStream(
    grpc::ServerContext* /*context*/,
    const vms::control::CameraRequest* request,
    vms::control::OperationStatus* response) {
  std::string error;
  const bool success = m_manager.stop_jpeg_stream(request->camera_id(), error);

  response->set_success(success);
  response->set_message(success ? "jpeg stream stopped" : error);
  return grpc::Status::OK;
}

grpc::Status camera_control_service::StreamJpegFrames(
    grpc::ServerContext* context,
    const vms::control::CameraRequest* request,
    grpc::ServerWriter<vms::control::JpegFrame>* writer) {
  std::vector<uint8_t> frame;
  uint64_t frame_number = 0;

  while (!context->IsCancelled()) {
    std::string error;
    if (!m_manager.wait_next_jpeg_frame(request->camera_id(), frame, frame_number, error)) {
      return grpc::Status(grpc::StatusCode::FAILED_PRECONDITION, error);
    }

    vms::control::JpegFrame msg;
    msg.set_jpeg_data(frame.data(), static_cast<int>(frame.size()));
    msg.set_frame_number(frame_number);

    if (!writer->Write(msg)) {
      break;
    }
  }

  return grpc::Status::OK;
}
