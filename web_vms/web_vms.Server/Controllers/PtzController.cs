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
  // ── PTZ presets ────────────────────────────────────────────────────────────

  [HttpGet("cameras/{cameraId}/presets")]
  public async Task<IActionResult> GetPresets(string cameraId, [FromQuery] string profileToken, CancellationToken ct)
  {
    var response = await ptzClient.GetPresetsAsync(
      new PtzPresetsRequest { SourceId = cameraId, ProfileToken = profileToken ?? string.Empty },
      Options(ct));
    return Ok(response.Presets.Select(p => new { token = p.Token, name = p.Name }));
  }

  [HttpPost("cameras/{cameraId}/presets")]
  public async Task<IActionResult> SetPreset(string cameraId, [FromBody] SetPresetBody body, CancellationToken ct)
  {
    var response = await ptzClient.SetPresetAsync(
      new PtzSetPresetRequest
      {
        SourceId     = cameraId,
        ProfileToken = body.ProfileToken,
        PresetName   = body.PresetName,
        PresetToken  = body.PresetToken ?? string.Empty,
      }, Options(ct));
    return response.Success
      ? Ok(new { presetToken = response.PresetToken })
      : StatusCode(502, response.Error);
  }

  [HttpPost("cameras/{cameraId}/presets/{presetToken}/goto")]
  public async Task<IActionResult> GotoPreset(
    string cameraId, string presetToken, [FromBody] GotoPresetBody body, CancellationToken ct)
  {
    var response = await ptzClient.GotoPresetAsync(
      new PtzGotoPresetRequest
      {
        SourceId     = cameraId,
        ProfileToken = body.ProfileToken,
        PresetToken  = presetToken,
      }, Options(ct));
    return response.Success ? Ok() : BadRequest(response.Error);
  }

  [HttpDelete("cameras/{cameraId}/presets/{presetToken}")]
  public async Task<IActionResult> RemovePreset(
    string cameraId, string presetToken, [FromQuery] string profileToken, CancellationToken ct)
  {
    var response = await ptzClient.RemovePresetAsync(
      new PtzRemovePresetRequest
      {
        SourceId     = cameraId,
        ProfileToken = profileToken ?? string.Empty,
        PresetToken  = presetToken,
      }, Options(ct));
    return response.Success ? Ok() : BadRequest(response.Error);
  }
}

public record PtzMoveDto(string CameraId, string? ProfileToken, float Pan, float Tilt, float Zoom);
public record PtzAbsoluteMoveDto(string CameraId, string? ProfileToken, float Pan, float Tilt, float Zoom, float Speed);
public record PtzStopDto(string CameraId, string? ProfileToken);
public record SetPresetBody(string ProfileToken, string PresetName, string? PresetToken);
public record GotoPresetBody(string ProfileToken);
