#include <algorithm>
#include <google/protobuf/timestamp.pb.h>
#include <google/protobuf/util/json_util.h>
#include <chrono>
#include <fstream>
#include <sstream>
#include <vector>

#include "camera_control_service.h"
#include "player_command_mapper.h"
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

grpc::Status camera_control_service::AddCamera(
    grpc::ServerContext* /*context*/,
    const vms::control::CameraState* request,
    vms::control::OperationStatus* response) {
  std::string error;
  const auto policy = proto_mapper::recording_policy_for_media(
    request->recording_mode(),
    proto_mapper::prealarm_segment_seconds(*request));
  const bool success = m_manager.add_camera(
    request->camera_id(),
    request->rtsp_url(),
    request->has_cred() ? request->cred().user() : "",
    request->has_cred() ? request->cred().pass() : "",
    policy,
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

  bool success = m_manager.add_arch_player(
    request->client_id(),
    request->camera_id(),
    error);

  if (success)
  {
    auto pos = request->has_position_ms() ?
      std::make_optional<std::chrono::milliseconds>(request->position_ms()) :
      std::nullopt;

    auto position_ts = request->has_position_ts() ?
      std::optional <std::chrono::system_clock::time_point>(to_time_point(request->position_ts())) :
      std::nullopt;

    success = m_manager.set_player_state(
      request->client_id(),
      request->camera_id(),
      position_ts,
      pos,
      error);
  }
  response->set_success(success);
  response->set_message(success ? "archive player added" : error);
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

  auto position_ts = request->has_position_ts() ?
    std::optional <std::chrono::system_clock::time_point>(to_time_point(request->position_ts())) :
    std::nullopt;

  const bool success = m_manager.set_player_state(
    request->client_id(),
    request->camera_id(),
    position_ts,
    pos,
    error);

  response->set_success(success);
  response->set_message(success ? "archive player state updated" : error);
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

grpc::Status camera_control_service::GetCameraState(
  grpc::ServerContext* /*context*/,
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
  response->set_recording_mode(
    proto_mapper::to_grpc_recording_mode(info->recording_policy));
  response->set_web_rtc(info->is_web_rtc_streaming);
  response->set_rtsp_url(info->rtsp_url);
  response->mutable_cred()->set_user(info->user);
  response->mutable_cred()->set_pass(info->pass);
  return grpc::Status::OK;
}

::grpc::Status camera_control_service::SendPlayerCommand(
  ::grpc::ServerContext* /*context*/,
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

grpc::Status camera_control_service::ListPlayers(grpc::ServerContext* /*context*/,
  const vms::control::ListPlayersRequest* /*request*/,
  vms::control::ListPlayersResponse* response)
{
  for (const auto& camera : m_manager.list_players())
  {
    auto* item = response->add_players();
    item->set_camera_id(camera.camera_id);
    item->set_client_id(camera.client_id);
    *item->mutable_position_ts() = from_time_point(camera.position_ts);

    if (camera.file_path)
    {
      *item->mutable_file_path() = camera.file_path->filename().string();
    }

    if (camera.start_ts)
    {
      *item->mutable_start_ts() = from_time_point(*camera.start_ts);
    }

    if (camera.progress)
    {
      item->set_duration_ms(camera.progress->duration.count());
      item->set_position_ms(camera.progress->position.count());
    }

    if (camera.mode)
    {
      item->set_mode(to_grpc_mode(*camera.mode));
    }
  }

  return grpc::Status::OK;
}

::grpc::Status camera_control_service::GetPlayerProgressState(
  ::grpc::ServerContext* /*context*/,
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

grpc::Status camera_control_service::GetRecordings(grpc::ServerContext* /*context*/,
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

grpc::Status camera_control_service::StatArchiveTs(
  grpc::ServerContext* /*context*/,
  const vms::control::StatArchiveTsRequest* request,
  vms::control::StatArchiveTsResponse* response)
{
  const auto abs = m_storage.resolve_archive_relative_path(request->relative_path());
  if (!abs)
  {
    response->set_exists(false);
    response->set_message("invalid path");
    return grpc::Status::OK;
  }

  std::uint64_t sz = 0;
  std::string err;
  if (!m_storage.stat_archive_file(*abs, sz, err))
  {
    response->set_exists(false);
    response->set_message(err.empty() ? "not found" : err);
    return grpc::Status::OK;
  }

  response->set_exists(true);
  response->set_size_bytes(sz);
  response->clear_message();
  return grpc::Status::OK;
}

grpc::Status camera_control_service::ReadArchiveTs(
  grpc::ServerContext* /*context*/,
  const vms::control::ReadArchiveTsRequest* request,
  grpc::ServerWriter<vms::control::ReadArchiveTsChunk>* writer)
{
  const auto abs = m_storage.resolve_archive_relative_path(request->relative_path());
  if (!abs)
    return grpc::Status(grpc::StatusCode::INVALID_ARGUMENT, "invalid path");

  std::uint64_t file_size = 0;
  std::string err;
  if (!m_storage.stat_archive_file(*abs, file_size, err))
    return grpc::Status(grpc::StatusCode::NOT_FOUND, err.empty() ? "not found" : err);

  const auto offset = request->offset();
  if (offset > file_size)
    return grpc::Status(grpc::StatusCode::OUT_OF_RANGE, "offset past end");

  std::uint64_t entity_len = file_size - offset;
  if (request->length() > 0)
    entity_len = std::min(entity_len, request->length());

  std::ifstream in(*abs, std::ios::binary);
  if (!in)
    return grpc::Status(grpc::StatusCode::NOT_FOUND, "open failed");

  in.seekg(static_cast<std::streamoff>(offset));

  constexpr std::size_t k_buf = 256 * 1024;
  std::vector<char> buf(k_buf);
  std::uint64_t remaining = entity_len;

  while (remaining > 0)
  {
    const auto chunk = static_cast<std::size_t>(std::min<std::uint64_t>(remaining, k_buf));
    in.read(buf.data(), static_cast<std::streamsize>(chunk));
    const auto got = static_cast<std::size_t>(in.gcount());
    if (got == 0)
      break;

    vms::control::ReadArchiveTsChunk msg;
    msg.set_data(buf.data(), static_cast<int>(got));
    if (!writer->Write(msg))
      return grpc::Status(grpc::StatusCode::ABORTED, "client closed stream");

    remaining -= got;
  }

  return grpc::Status::OK;
}

grpc::Status camera_control_service::GetArchiveTsClipIndex(
  grpc::ServerContext* /*context*/,
  const vms::control::GetArchiveTsClipIndexRequest* request,
  vms::control::GetArchiveTsClipIndexResponse* response)
{
  const auto abs = m_storage.resolve_archive_relative_path(request->relative_path());
  if (!abs)
  {
    response->set_found(false);
    response->set_message("invalid path");
    return grpc::Status::OK;
  }

  auto idx_path = *abs;
  idx_path += ".idx.json";

  std::ifstream in(idx_path);
  if (!in)
  {
    response->set_found(false);
    response->set_message("index file not found");
    return grpc::Status::OK;
  }

  std::ostringstream ss;
  ss << in.rdbuf();
  const auto json = ss.str();

  vms::control::TsClipIndex idx;
  const auto st = google::protobuf::util::JsonStringToMessage(json, &idx);
  if (!st.ok())
  {
    response->set_found(false);
    response->set_message(st.ToString());
    return grpc::Status::OK;
  }

  response->set_found(true);
  response->mutable_index()->Swap(&idx);
  response->clear_message();
  return grpc::Status::OK;
}
