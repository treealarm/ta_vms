using Grpc.Core;
using Microsoft.AspNetCore.Mvc;
using Vms.Onvif;

namespace web_vms;

[ApiController]
[Route("api/[controller]")]
public class PtzController(PtzService.PtzServiceClient ptzClient) : ControllerBase
{
  private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

  private static CallOptions Options(CancellationToken ct) =>
    new(deadline: DateTime.UtcNow.Add(Timeout), cancellationToken: ct);

  [HttpPost("continuous-move")]
  public async Task<IActionResult> ContinuousMove([FromBody] PtzMoveDto dto, CancellationToken ct)
  {
    var response = await ptzClient.ContinuousMoveAsync(new PtzContinuousMoveRequest
    {
      SourceId     = dto.CameraId,
      ProfileToken = dto.ProfileToken ?? string.Empty,
      Pan          = dto.Pan,
      Tilt         = dto.Tilt,
      Zoom         = dto.Zoom
    }, Options(ct));

    return response.Success ? Ok() : BadRequest(response.Error);
  }

  [HttpPost("absolute-move")]
  public async Task<IActionResult> AbsoluteMove([FromBody] PtzAbsoluteMoveDto dto, CancellationToken ct)
  {
    var response = await ptzClient.AbsoluteMoveAsync(new PtzAbsoluteMoveRequest
    {
      SourceId     = dto.CameraId,
      ProfileToken = dto.ProfileToken ?? string.Empty,
      Pan          = dto.Pan,
      Tilt         = dto.Tilt,
      Zoom         = dto.Zoom,
      Speed        = dto.Speed
    }, Options(ct));

    return response.Success ? Ok() : BadRequest(response.Error);
  }

  [HttpPost("relative-move")]
  public async Task<IActionResult> RelativeMove([FromBody] PtzMoveDto dto, CancellationToken ct)
  {
    var response = await ptzClient.RelativeMoveAsync(new PtzRelativeMoveRequest
    {
      SourceId     = dto.CameraId,
      ProfileToken = dto.ProfileToken ?? string.Empty,
      Pan          = dto.Pan,
      Tilt         = dto.Tilt,
      Zoom         = dto.Zoom
    }, Options(ct));

    return response.Success ? Ok() : BadRequest(response.Error);
  }

  [HttpGet("capabilities")]
  public async Task<IActionResult> GetCapabilities([FromQuery] string cameraId, CancellationToken ct)
  {
    var response = await ptzClient.GetCapabilitiesAsync(
      new PtzCapabilitiesRequest { SourceId = cameraId }, Options(ct));

    return Ok(new
    {
      absoluteMove   = response.AbsoluteMove,
      relativeMove   = response.RelativeMove,
      continuousMove = response.ContinuousMove,
    });
  }

  [HttpPost("stop")]
  public async Task<IActionResult> Stop([FromBody] PtzStopDto dto, CancellationToken ct)
  {
    var response = await ptzClient.StopAsync(new PtzStopRequest
    {
      SourceId     = dto.CameraId,
      ProfileToken = dto.ProfileToken ?? string.Empty
    }, Options(ct));

    return response.Success ? Ok() : BadRequest(response.Error);
  }
}

public record PtzMoveDto(string CameraId, string? ProfileToken, float Pan, float Tilt, float Zoom);
public record PtzAbsoluteMoveDto(string CameraId, string? ProfileToken, float Pan, float Tilt, float Zoom, float Speed);
public record PtzStopDto(string CameraId, string? ProfileToken);
