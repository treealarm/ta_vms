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
    if (!string.IsNullOrWhiteSpace(request.StartTime))
    {
      var startPlayResponse = await grpcClient.AddArchPlayerAsync(new PlayFileRequest
      {
        CameraId = request.CameraId,
        StartTime = request.StartTime,
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
    var cameras = response.Cameras.Select(static camera => new CameraDto(camera.CameraId, camera.RtspUrl, camera.IsRecording));
    return Ok(cameras);
  }

  [HttpPost("cameras/{cameraId}/recording/start")]
  public async Task<IActionResult> StartRecording(string cameraId, CancellationToken ct)
  {
    var response = await grpcClient.SetCameraStateAsync(new SetCameraStateRequest
    {
      CameraId = cameraId,
      State = new CameraState
      {
        Recording = true,
        WebRtc = true
      }
    }, cancellationToken: ct);

    return Ok(ToApiResponse(response));
  }

  [HttpPost("cameras/{cameraId}/state")]
  public async Task<IActionResult> SetCameraState(string cameraId, [FromBody] CameraStateDto state, CancellationToken ct)
  {
    var response = await grpcClient.SetCameraStateAsync(new SetCameraStateRequest
    {
      CameraId = cameraId,
      State = new CameraState
      {
        Recording = state.Recording,
        WebRtc = state.WebRtc
      }
    }, cancellationToken: ct);

    return Ok(ToApiResponse(response));
  }
  private static ApiResponse ToApiResponse(OperationStatus status) => new(status.Success, status.Message);
}

public record AddCameraDto(string CameraId,
                           string RtspUrl,
                           CameraStateDto? State,
                           string? StorageBasePath,
                           string? StartTime);

public record CameraDto(string CameraId, string RtspUrl, bool IsRecording);

public record CameraStateDto(bool Recording, bool WebRtc);

public record ApiResponse(bool Success, string Message);
