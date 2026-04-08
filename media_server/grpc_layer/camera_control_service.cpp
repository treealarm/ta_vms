#include <google/protobuf/timestamp.pb.h>
#include <chrono>

#include "camera_control_service.h"
#include "player_command_mapper.h"

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

grpc::Status camera_control_service::AddCamera(
    grpc::ServerContext* /*context*/,
    const vms::control::CameraState* request,
    vms::control::OperationStatus* response) {
  std::string error;
  const bool success = m_manager.add_camera(
    request->camera_id(),
    request->rtsp_url(),
    request->cred().user(),
    request->cred().pass(),
    request->recording(),
    request->web_rtc(),
    error);

  response->set_success(success);
  response->set_message(success ? "camera added" : error);
  return grpc::Status::OK;
}

grpc::Status camera_control_service::AddArchPlayer(
    grpc::ServerContext* /*context*/,
    const vms::control::PlayerState* request,
    vms::control::OperationStatus* response) 
{
  std::string error;

  if (request->client_id().empty())
  {
    response->set_success(false);
    response->set_message("archive player wrong client_id");
    return ::grpc::Status(::grpc::StatusCode::INVALID_ARGUMENT, "wrong client_id");
  }

  const bool success = m_manager.add_arch_player(
    request->client_id(),
    request->camera_id(),
    error);

  response->set_success(success);
  response->set_message(success ? "archive player added" : error);
  return grpc::Status::OK;
}

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

grpc::Status camera_control_service::RemoveArchPlayer(
    grpc::ServerContext* /*context*/,
    const vms::control::SourceRequest* request,
    vms::control::OperationStatus* response) {
  std::string error;

  const bool success = m_manager.remove_arch_player(request->source_id(), error);

  response->set_success(success);
  response->set_message(success ? "archive player removed" : error);
  return grpc::Status::OK;
}

grpc::Status camera_control_service::ListCameras(
    grpc::ServerContext* /*context*/,
    const vms::control::ListCamerasRequest* /*request*/,
    vms::control::ListCamerasResponse* response)
{
  for (const auto& camera : m_manager.list_cameras()) 
  {
    auto* item = response->add_cameras();
    item->set_camera_id(camera.camera_id);
    item->set_rtsp_url(camera.rtsp_url);
  }

  return grpc::Status::OK;
}

grpc::Status camera_control_service::ListPlayers(grpc::ServerContext* context,
  const vms::control::ListPlayersRequest* request,
  vms::control::ListPlayersResponse* response)
{
  for (const auto& camera : m_manager.list_players())
  {
    auto* item = response->add_players();
    item->set_camera_id(camera.camera_id);
    item->set_client_id(camera.client_id);
    *item->mutable_position_ts() = from_time_point(camera.position_ts);
  }

  return grpc::Status::OK;
}

grpc::Status camera_control_service::SetCameraState(
    grpc::ServerContext* /*context*/,
    const vms::control::CameraState* request,
    vms::control::OperationStatus* response) 
{
  std::string error;
  const bool success = m_manager.set_camera_state(
    request->camera_id(),
    request->rtsp_url(),
    request->cred().user(),
    request->cred().pass(),
    request->recording(),
    request->web_rtc(),
    error);

  response->set_success(success);
  response->set_message(success ? "camera state updated" : error);
  return grpc::Status::OK;
}

grpc::Status camera_control_service::GetCameraState(
  grpc::ServerContext* context,
  const vms::control::SourceRequest* request,
  vms::control::CameraState* response)
{
  std::string error;

  const auto info = m_manager.get_camera_state(
    request->source_id(),
    error);

  if (!info)
  {
    return ::grpc::Status(::grpc::StatusCode::INTERNAL, error);
  }
  response->set_camera_id(request->source_id());
  response->set_recording(info->is_recording);
  response->set_web_rtc(info->is_web_rtc_streaming);
  response->set_rtsp_url(info->rtsp_url);
  return grpc::Status::OK;
}

grpc::Status camera_control_service::SetPlayerState(
    grpc::ServerContext* /*context*/,
    const vms::control::PlayerState* request,
    vms::control::OperationStatus* response) 
{  
  std::string error;
  auto pos = request->has_position_ms() ? 
    std::make_optional<std::chrono::milliseconds>(request->position_ms()) :
    std::nullopt;

  const bool success = m_manager.set_player_state(
    request->client_id(),
    request->camera_id(),
    to_time_point(request->position_ts()),
    pos,
    error);

  response->set_success(success);
  response->set_message(success ? "archive player state updated" : error);
  return grpc::Status::OK;
}

::grpc::Status camera_control_service::SendPlayerCommand(
  ::grpc::ServerContext* context,
  const ::vms::control::PlayerCommand* request,
  ::vms::control::OperationStatus* response)
{
  std::string error;

  auto cmd = map_player_command(*request);

  if (!cmd)
  {
    response->set_success(false);
    response->set_message("archive player command not found");
    return ::grpc::Status(::grpc::StatusCode::NOT_FOUND, "player command not found");
  }

  const bool success = m_manager.set_player_command(
    request->client_id(),
    *cmd,
    //request->cmd_case(),
    error);

  response->set_success(success);
  response->set_message(success ? "archive player command sent" : error);
  return grpc::Status::OK;
}

::grpc::Status camera_control_service::GetPlayerProgressState(
  ::grpc::ServerContext* context,
  const ::vms::control::SourceRequest* request,
  ::vms::control::PlayerProgressState* response)
{
  std::string error;

  const auto entry = m_manager.get_player_state(
    request->source_id(),
    error);
  if (!entry)
  {
    return ::grpc::Status(::grpc::StatusCode::NOT_FOUND, "archive player not found");
  }
  response->set_client_id(entry->client_id);
  response->set_camera_id(entry->camera_id);
  *response->mutable_position_ts() = from_time_point(entry->position_ts);
  if (entry->file_path)
  {
    *response->mutable_file_path() = entry->file_path->filename().string();
  }

  if (entry->start_ts)
  {
    *response->mutable_start_ts() = from_time_point(*entry->start_ts);
  }
  

  if (entry->progress)
  {
    response->set_duration_ms(entry->progress->duration.count());
    response->set_position_ms(entry->progress->position.count());
  }

  if (entry->mode)
  {
    response->set_mode(to_grpc_mode(*entry->mode));
  }
  return grpc::Status::OK;
}
grpc::Status camera_control_service::GetRecordings(grpc::ServerContext* context,
  const vms::control::RecordingsRequest* request,
  vms::control::RecordingsResponse* response)
{
  int32_t n_nearest = -1;

  auto recordings = m_storage.get_recordings(
    request->camera_id(),
    to_time_point(request->timestamp()),
    request->limit(),
    n_nearest
  );
  
  if (n_nearest >= 0)
  {
    auto nearest = &recordings[n_nearest];
    auto recording = response->mutable_nearest();
    *recording->mutable_file_path() = nearest->file_path.filename().string();
    *recording->mutable_start_time() = from_time_point(nearest->start_time);
  }
  for (auto& r : recordings)
  {
    auto recording = response->add_recordings();
    *recording->mutable_file_path() = r.file_path.filename().string();
    *recording->mutable_start_time() = from_time_point(r.start_time);
  }

  return grpc::Status::OK;
}