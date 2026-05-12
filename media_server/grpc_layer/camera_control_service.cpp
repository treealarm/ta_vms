#include <algorithm>
#include <google/protobuf/timestamp.pb.h>
#include <chrono>
#include <vector>

#include "camera_control_service.h"
#include "proto_mapper.h"

std::chrono::system_clock::time_point to_time_point(
  const google::protobuf::Timestamp& ts)
{
  using namespace std::chrono;

  system_clock::duration d =
    duration_cast<system_clock::duration>(
      seconds(ts.seconds()) + nanoseconds(ts.nanos()));

  auto tp = system_clock::time_point(d);
  
  //std::chrono::zoned_time local_time{ std::chrono::current_zone(), tp };

  //std::cout << "Local time: "
  //  << std::format("{:%F %T}", local_time)
  //  << "\n";

  return tp;
}

google::protobuf::Timestamp from_time_point(
  const std::chrono::system_clock::time_point& tp)
{
  using namespace std::chrono;

  auto duration = tp.time_since_epoch();

  auto sec = duration_cast<seconds>(duration);
  auto nanos = duration_cast<nanoseconds>(duration - sec);

  google::protobuf::Timestamp ts;
  ts.set_seconds(sec.count());
  ts.set_nanos(nanos.count());

  return ts;
}

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
    request->rtsp_url(),
    request->has_cred() ? request->cred().user() : "",
    request->has_cred() ? request->cred().pass() : "",
    policy,
    request->web_rtc(),
    error);

  response->set_success(success);
  response->set_message(success ? "camera state updated" : error);
  return grpc::Status::OK;
}


grpc::Status camera_control_service::GetArchiveFrameJpeg(
  grpc::ServerContext* /*context*/,
  const vms::control::ArchiveFrameJpegRequest* request,
  vms::control::ArchiveFrameJpegResponse* response)
{
  if (request->camera_id().empty())
  {
    response->set_success(false);
    response->set_message("camera_id must not be empty");
    return ::grpc::Status(::grpc::StatusCode::INVALID_ARGUMENT, "camera_id must not be empty");
  }

  std::vector<uint8_t> jpeg;
  std::string error;

  archive_frame_jpeg_reader reader;
  const bool success = reader.read_frame_at(
    request->camera_id(),
    to_time_point(request->timestamp()),
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

