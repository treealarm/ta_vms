using Microsoft.AspNetCore.Mvc;
using Vms.Control;

namespace web_vms.Server.Controllers;

[ApiController]
[Route("api/vms")]
public class VmsController(CameraControlService.CameraControlServiceClient grpcClient) : ControllerBase
{
  [HttpPost("cameras")]
  public async Task<IActionResult> AddCamera([FromBody] AddCameraDto request, CancellationToken ct)
  {
    var response = await grpcClient.AddCameraAsync(new AddCameraRequest
    {
      CameraId = request.CameraId,
      RtspUrl = request.RtspUrl,
      StartImmediately = request.StartImmediately
    }, cancellationToken: ct);

    return Ok(ToApiResponse(response));
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
  public async Task<IActionResult> StartJpeg(string cameraId, [FromBody] StartJpegDto request, CancellationToken ct)
  {
    var response = await grpcClient.StartJpegStreamAsync(new JpegStreamRequest
    {
      CameraId = cameraId,
      Host = request.Host,
      Port = request.Port
    }, cancellationToken: ct);

    return Ok(ToApiResponse(response));
  }

  [HttpPost("cameras/{cameraId}/jpeg/stop")]
  public async Task<IActionResult> StopJpeg(string cameraId, CancellationToken ct)
  {
    var response = await grpcClient.StopJpegStreamAsync(new CameraRequest { CameraId = cameraId }, cancellationToken: ct);
    return Ok(ToApiResponse(response));
  }

  [HttpGet("cameras/{cameraId}/jpeg/frame")]
  public async Task<IActionResult> GetSingleFrame(string cameraId, CancellationToken ct)
  {
    using var call = grpcClient.StreamJpegFrames(new CameraRequest { CameraId = cameraId }, cancellationToken: ct);
    if (!await call.ResponseStream.MoveNext(ct))
    {
      return NotFound("Frame not available.");
    }

    var frame = call.ResponseStream.Current;
    Response.Headers.Append("X-Frame-Number", frame.FrameNumber.ToString());
    return File(frame.JpegData.ToByteArray(), "image/jpeg");
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

  private static ApiResponse ToApiResponse(OperationStatus status) => new(status.Success, status.Message);
}

public record AddCameraDto(string CameraId, string RtspUrl, bool StartImmediately);

public record StartJpegDto(string Host, uint Port);

public record ApiResponse(bool Success, string Message);
