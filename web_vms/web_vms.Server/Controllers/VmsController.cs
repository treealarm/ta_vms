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
      var stopResponse = await grpcClient.StopPlayFileAsync(new CameraRequest { CameraId = request.CameraId }, cancellationToken: ct);
      if (!stopResponse.Success && !stopResponse.Message.Contains("camera not found", StringComparison.OrdinalIgnoreCase))
      {
        return Ok(ToApiResponse(stopResponse));
      }

      var startPlayResponse = await grpcClient.StartPlayFileAsync(new PlayFileRequest
      {
        CameraId = request.CameraId,
        StartTime = request.StartTime
      }, cancellationToken: ct);

      return Ok(ToApiResponse(startPlayResponse));
    }

    var response = await grpcClient.AddCameraAsync(new AddCameraRequest
    {
      CameraId = request.CameraId,
      RtspUrl = request.RtspUrl,
      StartImmediately = request.StartImmediately,
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
    var response = await grpcClient.StartRecordingAsync(new CameraRequest { CameraId = cameraId }, cancellationToken: ct);
    return Ok(ToApiResponse(response));
  }

  [HttpPost("cameras/{cameraId}/recording/stop")]
  public async Task<IActionResult> StopRecording(string cameraId, CancellationToken ct)
  {
    var response = await grpcClient.StopRecordingAsync(new CameraRequest { CameraId = cameraId }, cancellationToken: ct);
    return Ok(ToApiResponse(response));
  }

  [HttpPost("cameras/{cameraId}/jpeg/start")]
  public async Task<IActionResult> StartJpeg(string cameraId, CancellationToken ct)
  {
    var response = await grpcClient.StartJpegStreamAsync(new JpegStreamRequest
    {
      CameraId = cameraId
    }, cancellationToken: ct);

    return Ok(ToApiResponse(response));
  }

  [HttpPost("cameras/{cameraId}/jpeg/stop")]
  public async Task<IActionResult> StopJpeg(string cameraId, CancellationToken ct)
  {
    var response = await grpcClient.StopJpegStreamAsync(new CameraRequest { CameraId = cameraId }, cancellationToken: ct);
    return Ok(ToApiResponse(response));
  }

  [HttpGet("cameras/{cameraId}/jpeg/mjpeg")]
  public async Task StreamFramesAsMjpeg(string cameraId, CancellationToken ct)
  {
    Response.StatusCode = StatusCodes.Status200OK;
    Response.ContentType = "multipart/x-mixed-replace; boundary=frame";

    using var call = grpcClient.StreamJpegFrames(new CameraRequest { CameraId = cameraId }, cancellationToken: ct);

    while (!ct.IsCancellationRequested && await call.ResponseStream.MoveNext(ct))
    {
      var frame = call.ResponseStream.Current;
      var data = frame.JpegData.ToByteArray();

      await Response.WriteAsync("--frame\r\n", ct);
      await Response.WriteAsync("Content-Type: image/jpeg\r\n", ct);
      await Response.WriteAsync($"Content-Length: {data.Length}\r\n", ct);
      await Response.WriteAsync($"X-Frame-Number: {frame.FrameNumber}\r\n\r\n", ct);
      await Response.Body.WriteAsync(data, ct);
      await Response.WriteAsync("\r\n", ct);
      await Response.Body.FlushAsync(ct);
    }
  }

  [HttpGet("cameras/{cameraId}/jpeg/ws")]
  public async Task StreamFramesOverWebSocket(string cameraId, CancellationToken ct)
  {
    if (!HttpContext.WebSockets.IsWebSocketRequest)
    {
      Response.StatusCode = StatusCodes.Status400BadRequest;
      await Response.WriteAsync("WebSocket request expected.", ct);
      return;
    }

    using var webSocket = await HttpContext.WebSockets.AcceptWebSocketAsync();
    using var call = grpcClient.StreamJpegFrames(new CameraRequest { CameraId = cameraId }, cancellationToken: ct);

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
                           bool StartImmediately,
                           string? StorageBasePath,
                           string? StartTime);

public record CameraDto(string CameraId, string RtspUrl, bool IsRecording);

public record ApiResponse(bool Success, string Message);
