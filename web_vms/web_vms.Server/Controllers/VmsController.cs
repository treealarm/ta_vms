using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Mvc;
using System.Net.WebSockets;
using Vms.Control;

namespace web_vms.Server.Controllers;

[ApiController]
[Route("api/vms")]
public class VmsController(
  CameraControlService.CameraControlServiceClient grpcClient) : ControllerBase
{
  [HttpPost("cameras")]
  public async Task<IActionResult> AddCamera([FromBody] AddCameraDto request, CancellationToken ct)
  {
    if (request.StartTime!= null)
    {
      var dt = request.StartTime ?? DateTime.UtcNow;

      if (dt.Kind != DateTimeKind.Utc)
        dt = DateTime.SpecifyKind(dt, DateTimeKind.Utc);

      var StartTimeStamp = Timestamp.FromDateTime(dt);

      var startPlayResponse = await grpcClient.AddArchPlayerAsync(new PlayFileRequest
      {
        CameraId = request.CameraId,
        StartTime = StartTimeStamp,
        ClientId = $"{request.CameraId}_arch"
      }, cancellationToken: ct);

      return Ok(ToApiResponse(startPlayResponse));
    }

    var response = await grpcClient.AddCameraAsync(new AddCameraRequest
    {
      CameraId = request.CameraId,
      RtspUrl = request.RtspUrl,
      State = new CameraState
      {
        Recording = request.State?.Recording ?? false,
        WebRtc = true
      },
      StorageBasePath = request.StorageBasePath ?? string.Empty
    }, cancellationToken: ct);

    return Ok(ToApiResponse(response));
  }

  [HttpDelete("cameras/{cameraId}")]
  public async Task<IActionResult> RemoveCamera(string cameraId, [FromQuery] bool isArchive, CancellationToken ct)
  {
    OperationStatus response;

    if (isArchive)
    {
      response = await grpcClient.RemoveArchPlayerAsync(new PlayFileRequest
      {
        CameraId = cameraId,
        ClientId = $"{cameraId}_arch"
      }, cancellationToken: ct);
    }
    else
    {
      response = await grpcClient.RemoveCameraAsync(new CameraRequest
      {
        CameraId = cameraId
      }, cancellationToken: ct);
    }

    return Ok(ToApiResponse(response));
  }

  [HttpGet("cameras")]
  public async Task<IActionResult> ListCameras(CancellationToken ct)
  {
    var response = await grpcClient.ListCamerasAsync(new ListCamerasRequest(), cancellationToken: ct);
    var cameras = response.Cameras.Select(static camera => new CameraDto(camera.CameraId, camera.RtspUrl));
    return Ok(cameras);
  }

  [HttpPost("cameras/{cameraId}/recording/start")]
  public async Task<IActionResult> StartRecording(string cameraId, CancellationToken ct)
  {
    var response = await grpcClient.SetCameraStateAsync(new CameraState
    {
      CameraId = cameraId,
      Recording = true,
      WebRtc = true
    }, cancellationToken: ct);

    return Ok(ToApiResponse(response));
  }

  [HttpPost("cameras/state")]
  public async Task<IActionResult> SetCameraState([FromBody] CameraStateDto state, CancellationToken ct)
  {
    var response = await grpcClient.SetCameraStateAsync(new CameraState
    {
      CameraId = state.CameraId,
      Recording = state.Recording,
      WebRtc = state.WebRtc
    }, cancellationToken: ct);

    return Ok(ToApiResponse(response));
  }

  [HttpGet("cameras/{cameraId}/state")]
  public async Task<IActionResult> GetCameraState(string cameraId, CancellationToken ct)
  {
    var grpcResponse = await grpcClient.GetCameraStateAsync(new CameraRequest
    {
      CameraId = cameraId,
      ClientId = string.Empty
    }, cancellationToken: ct);

    var dto = new CameraStateDto
    {
      Recording = grpcResponse.Recording,
      WebRtc = grpcResponse.WebRtc,
      CameraId = grpcResponse.CameraId
    };

    return Ok(dto);
  }

  private static ApiResponse ToApiResponse(OperationStatus status) => new(status.Success, status.Message);
}

public record AddCameraDto(string CameraId,
                           string RtspUrl,
                           CameraStateDto? State,
                           string? StorageBasePath,
                           DateTime? StartTime);

public record CameraDto(string CameraId, string RtspUrl);

public record CameraStateDto
{
  required public string CameraId { get; init; }
  public bool Recording { get; init; }
  public bool WebRtc { get; init; }
}

public record ApiResponse(bool Success, string Message);

