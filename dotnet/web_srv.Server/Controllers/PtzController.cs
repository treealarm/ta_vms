using Grpc.Core;
using Microsoft.AspNetCore.Mvc;
using Vms.Onvif;
using web_srv.Authorization;

namespace web_srv;

[ApiController]
[Route("api/[controller]")]
public class PtzController(OnvifRouter router) : ControllerBase
{
  private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

  private static CallOptions Options(CancellationToken ct) =>
    new(deadline: DateTime.UtcNow.Add(Timeout), cancellationToken: ct);

  [HttpPost("continuous-move")]
  [CameraAccess(CameraAction.Ptz, Source = CameraIdSource.Body)]
  public async Task<IActionResult> ContinuousMove([FromBody] PtzMoveDto dto, CancellationToken ct)
  {
    var response = await (await router.PtzAsync(dto.CameraId, ct)).ContinuousMoveAsync(new PtzContinuousMoveRequest
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
  [CameraAccess(CameraAction.Ptz, Source = CameraIdSource.Body)]
  public async Task<IActionResult> AbsoluteMove([FromBody] PtzAbsoluteMoveDto dto, CancellationToken ct)
  {
    var response = await (await router.PtzAsync(dto.CameraId, ct)).AbsoluteMoveAsync(new PtzAbsoluteMoveRequest
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
  [CameraAccess(CameraAction.Ptz, Source = CameraIdSource.Body)]
  public async Task<IActionResult> RelativeMove([FromBody] PtzMoveDto dto, CancellationToken ct)
  {
    var response = await (await router.PtzAsync(dto.CameraId, ct)).RelativeMoveAsync(new PtzRelativeMoveRequest
    {
      SourceId     = dto.CameraId,
      ProfileToken = dto.ProfileToken ?? string.Empty,
      Pan          = dto.Pan,
      Tilt         = dto.Tilt,
      Zoom         = dto.Zoom
    }, Options(ct));

    return response.Success ? Ok() : BadRequest(response.Error);
  }

  // Reading what the camera supports drives the live view layout, so it follows viewing rights.
  [HttpGet("capabilities")]
  [CameraAccess(CameraAction.ViewLive)]
  public async Task<IActionResult> GetCapabilities([FromQuery] string cameraId, CancellationToken ct)
  {
    var response = await (await router.PtzAsync(cameraId, ct)).GetCapabilitiesAsync(
      new PtzCapabilitiesRequest { SourceId = cameraId }, Options(ct));

    return Ok(new
    {
      absoluteMove   = response.AbsoluteMove,
      relativeMove   = response.RelativeMove,
      continuousMove = response.ContinuousMove,
    });
  }

  /// <summary>Where the camera says it is pointing.</summary>
  /// <remarks>
  /// Under <see cref="CameraAction.Ptz"/> rather than viewing rights, unlike capabilities beside
  /// it: this is read while driving the camera by hand to record a position, and it is only useful
  /// to someone who may drive it.
  /// <para>
  /// A camera answering here is not proof its numbers mean anything — firmware that reports
  /// constants while the motor turns is real and was found on this project's own test camera. Any
  /// feature that depends on the position must confirm it by moving the camera and watching these
  /// change.
  /// </para>
  /// </remarks>
  [HttpGet("status")]
  [CameraAccess(CameraAction.Ptz)]
  public async Task<IActionResult> GetStatus(
    [FromQuery] string cameraId, [FromQuery] string? profileToken, CancellationToken ct)
  {
    var response = await (await router.PtzAsync(cameraId, ct)).GetStatusAsync(
      new PtzStatusRequest { SourceId = cameraId, ProfileToken = profileToken ?? string.Empty },
      Options(ct));

    return Ok(new
    {
      available = response.Available,
      pan       = response.Pan,
      tilt      = response.Tilt,
      zoom      = response.Zoom,
      moving    = response.Moving,
      error     = response.Error,
    });
  }

  [HttpPost("stop")]
  [CameraAccess(CameraAction.Ptz, Source = CameraIdSource.Body)]
  public async Task<IActionResult> Stop([FromBody] PtzStopDto dto, CancellationToken ct)
  {
    var response = await (await router.PtzAsync(dto.CameraId, ct)).StopAsync(new PtzStopRequest
    {
      SourceId     = dto.CameraId,
      ProfileToken = dto.ProfileToken ?? string.Empty
    }, Options(ct));

    return response.Success ? Ok() : BadRequest(response.Error);
  }
  // ── PTZ presets ────────────────────────────────────────────────────────────

  [HttpGet("cameras/{cameraId}/presets")]
  [CameraAccess(CameraAction.Ptz)]
  public async Task<IActionResult> GetPresets(string cameraId, [FromQuery] string profileToken, CancellationToken ct)
  {
    var response = await (await router.PtzAsync(cameraId, ct)).GetPresetsAsync(
      new PtzPresetsRequest { SourceId = cameraId, ProfileToken = profileToken ?? string.Empty },
      Options(ct));
    return Ok(response.Presets.Select(p => new { token = p.Token, name = p.Name }));
  }

  [HttpPost("cameras/{cameraId}/presets")]
  [CameraAccess(CameraAction.Ptz)]
  public async Task<IActionResult> SetPreset(string cameraId, [FromBody] SetPresetBody body, CancellationToken ct)
  {
    var response = await (await router.PtzAsync(cameraId, ct)).SetPresetAsync(
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
  [CameraAccess(CameraAction.Ptz)]
  public async Task<IActionResult> GotoPreset(
    string cameraId, string presetToken, [FromBody] GotoPresetBody body, CancellationToken ct)
  {
    var response = await (await router.PtzAsync(cameraId, ct)).GotoPresetAsync(
      new PtzGotoPresetRequest
      {
        SourceId     = cameraId,
        ProfileToken = body.ProfileToken,
        PresetToken  = presetToken,
      }, Options(ct));
    return response.Success ? Ok() : BadRequest(response.Error);
  }

  [HttpDelete("cameras/{cameraId}/presets/{presetToken}")]
  [CameraAccess(CameraAction.Ptz)]
  public async Task<IActionResult> RemovePreset(
    string cameraId, string presetToken, [FromQuery] string profileToken, CancellationToken ct)
  {
    var response = await (await router.PtzAsync(cameraId, ct)).RemovePresetAsync(
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
