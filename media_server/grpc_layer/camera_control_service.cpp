#include <vector>

#include "camera_control_service.h"
#include "proto_mapper.h"

camera_control_service::camera_control_service(camera_manager& manager)
    : m_manager(manager) {}

grpc::Status camera_control_service::RemoveCamera(
    grpc::ServerContext* /*context*/,
    const vms::control::SourceRequest* request,
    vms::control::OperationStatus* response) {
  std::string error;
  const bool success = m_manager.remove_camera(request->source_id(), error);

  response->set_success(success);
  response->set_message(success ? "camera removed" : error);
  return grpc::Status::OK;
}

grpc::Status camera_control_service::SetCameraState(
    grpc::ServerContext* /*context*/,
    const vms::control::CameraState* request,
    vms::control::OperationStatus* response)
{
  std::string error;
  const auto policy = proto_mapper::recording_policy_for_media(
    request->recording_mode(),
    proto_mapper::prealarm_segment_seconds(*request));
  const bool success = m_manager.set_camera_state(
    request->camera_id(),
    request->stream_name(),
    request->rtsp_url(),
    request->has_cred() ? request->cred().user() : "",
    request->has_cred() ? request->cred().pass() : "",
    policy,
    request->web_rtc(),
    error,
    request->webrtc_rtsp_push_url());

  response->set_success(success);
  response->set_message(success ? "camera state updated" : error);
  return grpc::Status::OK;
}


grpc::Status camera_control_service::GetArchiveFrameJpeg(
  grpc::ServerContext* /*context*/,
  const vms::control::ArchiveFrameJpegRequest* request,
  vms::control::ArchiveFrameJpegResponse* response)
{
  if (request->file_path().empty())
  {
    response->set_success(false);
    response->set_message("file_path must not be empty");
    return ::grpc::Status(::grpc::StatusCode::INVALID_ARGUMENT, "file_path must not be empty");
  }

  std::vector<uint8_t> jpeg;
  std::string error;

  archive_frame_jpeg_reader reader;
  const bool success = reader.read_frame_at(
    request->file_path(),
    request->offset_ms(),
    jpeg,
    error);

  response->set_success(success);

  if (!success)
  {
    response->set_message(error.empty() ? "failed to read archive jpeg frame" : error);
    return ::grpc::Status(::grpc::StatusCode::NOT_FOUND, response->message());
  }

  response->set_message("ok");
  response->set_mime_type("image/jpeg");
  response->set_jpeg(jpeg.data(), static_cast<int>(jpeg.size()));

  return grpc::Status::OK;
}
