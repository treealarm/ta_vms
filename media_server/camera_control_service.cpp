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
                                            request->state().web_rtc(),
                                            error);

  response->set_success(success);
  response->set_message(success ? "camera added" : error);
  return grpc::Status::OK;
}

grpc::Status camera_control_service::AddArchPlayer(
    grpc::ServerContext* /*context*/,
    const vms::control::PlayFileRequest* request,
    vms::control::OperationStatus* response) {
  std::string error;
  const bool jpeg_stream = request->has_jpeg_stream() ? request->jpeg_stream() : true;
  const std::string client_id = request->client_id().empty()
      ? request->camera_id() + "_arch"
      : request->client_id();
  const bool success = m_manager.add_arch_player(request->camera_id(),
                                                 client_id,
                                                 request->start_time(),
                                                 std::string(),
                                                 jpeg_stream,
                                                 error);

  response->set_success(success);
  response->set_message(success ? "archive player added" : error);
  return grpc::Status::OK;
}

grpc::Status camera_control_service::RemoveCamera(
    grpc::ServerContext* /*context*/,
    const vms::control::CameraRequest* request,
    vms::control::OperationStatus* response) {
  std::string error;
  const bool success = m_manager.remove_camera(request->camera_id(), error);

  response->set_success(success);
  response->set_message(success ? "camera removed" : error);
  return grpc::Status::OK;
}

grpc::Status camera_control_service::RemoveArchPlayer(
    grpc::ServerContext* /*context*/,
    const vms::control::PlayFileRequest* request,
    vms::control::OperationStatus* response) {
  std::string error;
  const std::string client_id = request->client_id().empty()
      ? request->camera_id() + "_arch"
      : request->client_id();
  const bool success = m_manager.remove_arch_player(client_id, error);

  response->set_success(success);
  response->set_message(success ? "archive player removed" : error);
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

grpc::Status camera_control_service::SetCameraState(
    grpc::ServerContext* /*context*/,
    const vms::control::SetCameraStateRequest* request,
    vms::control::OperationStatus* response) {
  std::string error;
  const bool success = m_manager.set_camera_state(request->camera_id(),
                                                  request->state().recording(),
                                                  request->state().jpeg_stream(),
                                                  request->state().web_rtc(),
                                                  error);

  response->set_success(success);
  response->set_message(success ? "camera state updated" : error);
  return grpc::Status::OK;
}

grpc::Status camera_control_service::SetPlayerState(
    grpc::ServerContext* /*context*/,
    const vms::control::PlayFileRequest* request,
    vms::control::OperationStatus* response) {
  std::string error;
  const bool jpeg_stream = request->has_jpeg_stream() ? request->jpeg_stream() : true;
  const std::string client_id = request->client_id().empty()
      ? request->camera_id() + "_arch"
      : request->client_id();
  const bool success = m_manager.set_player_state(client_id,
                                                  request->camera_id(),
                                                  request->start_time(),
                                                  jpeg_stream,
                                                  error);

  response->set_success(success);
  response->set_message(success ? "archive player state updated" : error);
  return grpc::Status::OK;
}

grpc::Status camera_control_service::StreamJpegFrames(
    grpc::ServerContext* context,
    const vms::control::CameraRequest* request,
    grpc::ServerWriter<vms::control::JpegFrame>* writer) {
  while (!context->IsCancelled()) {
    std::vector<uint8_t> frame;
    uint64_t frame_number = 0;
    std::string error;

    const std::string stream_id = request->client_id().empty()
        ? request->camera_id()
        : request->client_id();

    if (!m_manager.wait_next_jpeg_frame(stream_id, frame, frame_number, error)) {
      return grpc::Status(grpc::StatusCode::FAILED_PRECONDITION, error);
    }

    vms::control::JpegFrame response_frame;
    response_frame.set_jpeg_data(frame.data(), static_cast<int>(frame.size()));
    response_frame.set_frame_number(frame_number);

    if (!writer->Write(response_frame)) {
      break;
    }
  }

  return grpc::Status::OK;
}
