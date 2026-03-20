#include <google/protobuf/timestamp.pb.h>
#include <chrono>

#include "camera_control_service.h"

std::chrono::system_clock::time_point to_time_point(
  const google::protobuf::Timestamp& ts)
{
  using namespace std::chrono;

  system_clock::duration d =
    duration_cast<system_clock::duration>(
      seconds(ts.seconds()) + nanoseconds(ts.nanos()));

  return system_clock::time_point(d);
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

grpc::Status camera_control_service::AddCamera(
    grpc::ServerContext* /*context*/,
    const vms::control::AddCameraRequest* request,
    vms::control::OperationStatus* response) {
  std::string error;
  const bool success = m_manager.add_camera(request->camera_id(),
                                            request->rtsp_url(),
                                            request->storage_base_path(),
                                            request->state().recording(),
                                            request->state().web_rtc(),
                                            error);

  response->set_success(success);
  response->set_message(success ? "camera added" : error);
  return grpc::Status::OK;
}

grpc::Status camera_control_service::AddArchPlayer(
    grpc::ServerContext* /*context*/,
    const vms::control::PlayFileRequest* request,
    vms::control::OperationStatus* response) 
{
  std::string error;

  if (request->client_id().empty())
  {
    response->set_success(false);
    response->set_message("archive player wrong client_id");
    return grpc::Status::CANCELLED;
  }

  const bool success = m_manager.add_arch_player(
    request->client_id(),
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

  const std::string client_id = request->client_id().empty()
      ? request->camera_id() + "_arch"
      : request->client_id();
  const bool success = m_manager.set_player_state(
    client_id,
    request->camera_id(),
    to_time_point(request->start_time()),
    error);

  response->set_success(success);
  response->set_message(success ? "archive player state updated" : error);
  return grpc::Status::OK;
}
