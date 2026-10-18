#include <chrono>
#include <vector>

#include "camera_control_service.h"
#include "edge_import/edge_import_manager.h"
#include "proto_mapper.h"

camera_control_service::camera_control_service(camera_manager& manager)
    : m_manager(manager) {}

grpc::Status camera_control_service::RemoveCamera(
    grpc::ServerContext* /*context*/,
    const vms::control::SourceRequest* request,
    vms::control::OperationStatus* response) {
  const bool success = m_manager.remove_camera(request->source_id());

  response->set_success(success);
  response->set_message(success ? "camera removed" : "camera not found");
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
    policy,
    request->has_web_rtc() ? std::optional<bool>(request->web_rtc()) : std::nullopt,
    error);

  response->set_success(success);
  response->set_message(success ? "camera state updated" : error);
  return grpc::Status::OK;
}

grpc::Status camera_control_service::EnsureWebRtcStreaming(
    grpc::ServerContext* /*context*/,
    const vms::control::EnsureWebRtcStreamingRequest* request,
    vms::control::OperationStatus* response)
{
  std::string error;
  const bool success = m_manager.ensure_web_rtc(
    request->camera_id(), request->stream_name(), error);

  response->set_success(success);
  response->set_message(success ? "ok" : error);
  return grpc::Status::OK;
}

grpc::Status camera_control_service::EnsureAnalyticsPush(
    grpc::ServerContext* /*context*/,
    const vms::control::EnsureAnalyticsPushRequest* request,
    vms::control::OperationStatus* response)
{
  std::string error;
  const bool success = m_manager.ensure_analytics_push(
    request->camera_id(), request->stream_name(), request->rtsp_url(), request->sample_fps(),
    error);

  response->set_success(success);
  response->set_message(success ? "ok" : error);
  return grpc::Status::OK;
}

grpc::Status camera_control_service::StopAnalyticsPush(
    grpc::ServerContext* /*context*/,
    const vms::control::AnalyticsPushKey* request,
    vms::control::OperationStatus* response)
{
  std::string error;
  const bool success = m_manager.stop_analytics_push(
    request->camera_id(), request->stream_name(), error);

  response->set_success(success);
  response->set_message(success ? "ok" : error);
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

namespace {

std::chrono::system_clock::time_point from_unix_ms(int64_t ms)
{
  return std::chrono::system_clock::time_point(std::chrono::milliseconds(ms));
}

int64_t to_unix_ms(std::chrono::system_clock::time_point tp)
{
  return std::chrono::duration_cast<std::chrono::milliseconds>(tp.time_since_epoch()).count();
}

vms::control::EdgeImportState to_proto_state(edge_import_state state)
{
  switch (state)
  {
    case edge_import_state::running:   return vms::control::EDGE_IMPORT_STATE_RUNNING;
    case edge_import_state::completed: return vms::control::EDGE_IMPORT_STATE_COMPLETED;
    case edge_import_state::failed:    return vms::control::EDGE_IMPORT_STATE_FAILED;
    case edge_import_state::cancelled: return vms::control::EDGE_IMPORT_STATE_CANCELLED;
  }
  return vms::control::EDGE_IMPORT_STATE_UNSPECIFIED;
}

void fill_job(const edge_import_status& status, vms::control::EdgeImportJob* out)
{
  out->set_job_id(status.job_id);
  out->set_camera_id(status.camera_id);
  out->set_stream_name(status.stream_name);
  out->set_state(to_proto_state(status.state));
  out->set_start_unix_ms(to_unix_ms(status.start_ts));
  out->set_end_unix_ms(to_unix_ms(status.end_ts));
  out->set_position_unix_ms(to_unix_ms(status.position));
  out->set_clips_written(status.clips_written);
  out->set_bytes_written(status.bytes_written);
  out->set_message(status.message);
  out->set_export_path(status.export_path);
}

}  // namespace

grpc::Status camera_control_service::StartEdgeImport(
  grpc::ServerContext* /*context*/,
  const vms::control::EdgeImportRequest* request,
  vms::control::EdgeImportJob* response)
{
  edge_import_settings settings;
  settings.camera_id = request->camera_id();
  settings.stream_name = request->stream_name();
  settings.replay_uri = request->replay_uri();
  if (request->has_cred())
  {
    settings.user = request->cred().user();
    settings.password = request->cred().pass();
  }
  settings.start_ts = from_unix_ms(request->start_unix_ms());
  settings.end_ts = from_unix_ms(request->end_unix_ms());
  settings.recording_start_ts = from_unix_ms(request->recording_start_unix_ms());
  // Absent = the gateway could not measure the camera's clock offset, which it only allows when the
  // two clocks agree; the window then goes to the device unchanged.
  if (request->has_seek_start_unix_ms())
  {
    settings.seek_start_ts = from_unix_ms(request->seek_start_unix_ms());
  }
  if (request->has_seek_end_unix_ms())
  {
    settings.seek_end_ts = from_unix_ms(request->seek_end_unix_ms());
  }
  settings.export_to_file = request->export_to_file();
  if (request->segment_seconds() > 0)
  {
    settings.segment_duration = std::chrono::seconds(request->segment_seconds());
  }
  settings.export_chunk_duration = std::chrono::seconds(
    request->export_chunk_seconds() > 0 ? request->export_chunk_seconds()
                                        : request->segment_seconds());

  std::string error;
  const auto started = edge_import_manager::instance().start(std::move(settings), error);
  if (!started)
  {
    return grpc::Status(grpc::StatusCode::FAILED_PRECONDITION, error);
  }
  fill_job(*started, response);
  return grpc::Status::OK;
}

grpc::Status camera_control_service::GetEdgeImportJob(
  grpc::ServerContext* /*context*/,
  const vms::control::EdgeImportJobKey* request,
  vms::control::EdgeImportJob* response)
{
  const auto status = edge_import_manager::instance().get(request->job_id());
  if (!status)
  {
    return grpc::Status(grpc::StatusCode::NOT_FOUND, "unknown edge import job");
  }
  fill_job(*status, response);
  return grpc::Status::OK;
}

grpc::Status camera_control_service::CancelEdgeImport(
  grpc::ServerContext* /*context*/,
  const vms::control::EdgeImportJobKey* request,
  vms::control::OperationStatus* response)
{
  const bool cancelled = edge_import_manager::instance().cancel(request->job_id());
  response->set_success(cancelled);
  response->set_message(cancelled ? "cancelling" : "job is unknown or already finished");
  return grpc::Status::OK;
}

grpc::Status camera_control_service::ListEdgeImportJobs(
  grpc::ServerContext* /*context*/,
  const vms::control::ListEdgeImportJobsRequest* request,
  vms::control::ListEdgeImportJobsResponse* response)
{
  for (const auto& status : edge_import_manager::instance().list(request->camera_id()))
  {
    fill_job(status, response->add_jobs());
  }
  return grpc::Status::OK;
}
