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
        ClientId = $"{request.CameraId}_arch",
        JpegStream = true
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
        JpegStream = request.State?.JpegStream ?? false
      },
      StorageBasePath = request.StorageBasePath ?? string.Empty
    }, cancellationToken: ct);

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
        JpegStream = false
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
        JpegStream = state.JpegStream
      }
    }, cancellationToken: ct);

    return Ok(ToApiResponse(response));
  }

  [HttpGet("cameras/{cameraId}/jpeg/ws")]
  public async Task StreamFramesOverWebSocket(string cameraId, [FromQuery] string? clientId, CancellationToken ct)
  {
    if (!HttpContext.WebSockets.IsWebSocketRequest)
    {
      Response.StatusCode = StatusCodes.Status400BadRequest;
      await Response.WriteAsync("WebSocket request expected.", ct);
      return;
    }

    using var webSocket = await HttpContext.WebSockets.AcceptWebSocketAsync();
    using var call = grpcClient.StreamJpegFrames(new CameraRequest
    {
      CameraId = cameraId,
      ClientId = clientId ?? string.Empty
    }, cancellationToken: ct);

    while (!ct.IsCancellationRequested && webSocket.State == WebSocketState.Open && await call.ResponseStream.MoveNext(ct))
    {
      var frameData = call.ResponseStream.Current.JpegData.Memory;
      await webSocket.SendAsync(frameData, WebSocketMessageType.Binary, true, ct);
    }
  }

  private static ApiResponse ToApiResponse(OperationStatus status) => new(status.Success, status.Message);
}

public record AddCameraDto(string CameraId,
                           string RtspUrl,
                           CameraStateDto? State,
                           string? StorageBasePath,
                           string? StartTime);

public record CameraDto(string CameraId, string RtspUrl, bool IsRecording);

public record CameraStateDto(bool Recording, bool JpegStream);

public record ApiResponse(bool Success, string Message);
