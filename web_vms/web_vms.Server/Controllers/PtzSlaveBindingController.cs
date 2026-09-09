using Grpc.Core;
using Microsoft.AspNetCore.Mvc;
using Vms.Cfg;
using Vms.PtzTracking;
using web_vms.Authorization;
using web_vms.Types.Dto;

namespace web_vms;

/// <summary>
/// The survey that lets one camera be aimed at another camera's objects.
/// </summary>
/// <remarks>
/// <para>
/// Addressed by the slave throughout: it has one motor, so it takes hints from one master, and the
/// binding is a property of the camera being driven. A master may feed several slaves.
/// </para>
/// <para>
/// Guarded by <see cref="CameraAction.Ptz"/> on the slave, the same right the joystick carries —
/// all of this ends in that camera's motor turning. Marking out a survey also means watching the
/// master, and the wizard shows its picture through the ordinary live endpoints, which carry their
/// own <see cref="CameraAction.ViewLive"/> check; a caller who cannot see the master simply has no
/// frame to mark on.
/// </para>
/// </remarks>
[ApiController]
[Route("api/ptz-slave-binding")]
public class PtzSlaveBindingController(
  CfgService.CfgServiceClient _cfg,
  PtzTrackingService.PtzTrackingServiceClient _tracking) : ControllerBase
{
  // Long enough to cover a camera actually travelling: a test aim waits for the slave to be sent,
  // and a preset move on a slow camera is not instant.
  private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

  private static CallOptions Options(CancellationToken ct) =>
    new(deadline: DateTime.UtcNow.Add(Timeout), cancellationToken: ct);

  [HttpGet("{cameraId}")]
  [CameraAccess(CameraAction.Ptz)]
  public async Task<IActionResult> Get(string cameraId, CancellationToken ct)
  {
    var response = await _cfg.GetPtzSlaveBindingAsync(
      new GetPtzSlaveBindingRequest { SlaveCameraId = cameraId }, cancellationToken: ct);

    // A camera with no binding is not an error, it is a camera nobody has surveyed. 404 would make
    // the settings page treat "not set up" as "could not load".
    if (!response.Found)
      return Ok(null as PtzSlaveBindingDto);

    // The residuals come from the tracking service, which owns the fit. A survey the operator is
    // still building routinely cannot be fitted at all, so this failing is not the request failing.
    GetSlaveBindingFitResponse? fit = null;
    try
    {
      fit = await _tracking.GetSlaveBindingFitAsync(
        new GetSlaveBindingFitRequest { SlaveCameraId = cameraId }, Options(ct));
    }
    catch (RpcException)
    {
      // The tracking service is not up. The survey is still worth showing and editing without the
      // numbers beside it.
    }

    return Ok(ToDto(response.Binding, fit));
  }

  [HttpPut("{cameraId}")]
  [CameraAccess(CameraAction.Ptz)]
  public async Task<IActionResult> Put(
    string cameraId, [FromBody] PtzSlaveBindingDto dto, CancellationToken ct)
  {
    var binding = new PtzSlaveBinding
    {
      SlaveCameraId  = cameraId,
      MasterCameraId = dto.MasterCameraId,
      Method = dto.Method.Equals("presets", StringComparison.OrdinalIgnoreCase)
        ? PtzSlaveBindingMethod.PtzSlaveBindingPresets
        : PtzSlaveBindingMethod.PtzSlaveBindingAbsolute,
      CalibratedAt = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTime(DateTime.UtcNow),
    };

    foreach (var point in dto.Points)
    {
      var proto = new PtzSlaveBindingPoint
      {
        MasterX     = point.MasterX,
        MasterY     = point.MasterY,
        PresetToken = point.PresetToken ?? string.Empty,
        PresetName  = point.PresetName ?? string.Empty,
      };
      if (point.Pan is not null) proto.Pan = point.Pan.Value;
      if (point.Tilt is not null) proto.Tilt = point.Tilt.Value;
      binding.Points.Add(proto);
    }

    var status = await _cfg.UpsertPtzSlaveBindingAsync(
      new UpsertPtzSlaveBindingRequest { Binding = binding }, cancellationToken: ct);

    return status.Success ? Ok() : BadRequest(status.Message);
  }

  [HttpDelete("{cameraId}")]
  [CameraAccess(CameraAction.Ptz)]
  public async Task<IActionResult> Delete(string cameraId, CancellationToken ct)
  {
    var status = await _cfg.RemovePtzSlaveBindingAsync(
      new RemovePtzSlaveBindingRequest { SlaveCameraId = cameraId }, cancellationToken: ct);

    return status.Success ? Ok() : NotFound();
  }

  /// <summary>Sends the slave where a spot in the master's frame maps to.</summary>
  /// <remarks>
  /// How a survey is checked without waiting for someone to walk through it. It goes to
  /// VmsPtzTrackingSingleton rather than to the camera directly because that service is the one
  /// owner of a camera's motor — aiming from here would fight a running session for it.
  /// </remarks>
  [HttpPost("{cameraId}/aim")]
  [CameraAccess(CameraAction.Ptz)]
  public async Task<IActionResult> Aim(
    string cameraId, [FromBody] PtzSlaveAimDto dto, CancellationToken ct)
  {
    var response = await _tracking.AimAtMasterPointAsync(
      new AimAtMasterPointRequest
      {
        SlaveCameraId = cameraId,
        MasterX       = dto.MasterX,
        MasterY       = dto.MasterY,
      }, Options(ct));

    if (!response.Success)
      return BadRequest(response.Error);

    return Ok(new PtzSlaveAimResultDto
    {
      Pan          = response.HasPan ? response.Pan : null,
      Tilt         = response.HasTilt ? response.Tilt : null,
      PresetToken  = string.IsNullOrEmpty(response.PresetToken) ? null : response.PresetToken,
      PresetName   = string.IsNullOrEmpty(response.PresetName) ? null : response.PresetName,
      Extrapolated = response.Extrapolated,
    });
  }

  /// <summary>Keeps a slave from aiming itself while its binding is being marked out.</summary>
  /// <remarks>
  /// Held for a short time and renewed by the page rather than switched on and off, so a browser
  /// that was closed mid-survey cannot leave the camera parked. Sending zero releases it.
  /// </remarks>
  [HttpPost("{cameraId}/hold")]
  [CameraAccess(CameraAction.Ptz)]
  public async Task<IActionResult> Hold(
    string cameraId, [FromBody] PtzSlaveHoldDto dto, CancellationToken ct)
  {
    var response = await _tracking.HoldForSurveyAsync(
      new HoldForSurveyRequest { SlaveCameraId = cameraId, Seconds = dto.Seconds }, Options(ct));

    return response.Success ? Ok() : BadRequest(response.Error);
  }

  private static PtzSlaveBindingDto ToDto(PtzSlaveBinding binding, GetSlaveBindingFitResponse? fit)
  {
    var residuals = fit?.Residuals.ToDictionary(r => r.Ordinal, r => r.HasResidual ? r.Residual : (float?)null);

    return new PtzSlaveBindingDto
    {
      MasterCameraId = binding.MasterCameraId,
      Method = binding.Method == PtzSlaveBindingMethod.PtzSlaveBindingPresets ? "presets" : "absolute",
      CalibratedAt = binding.CalibratedAt?.ToDateTime(),
      Unusable = string.IsNullOrEmpty(fit?.Unusable) ? null : fit.Unusable,
      Points = binding.Points.Select((p, i) => new PtzSlaveBindingPointDto
      {
        MasterX     = p.MasterX,
        MasterY     = p.MasterY,
        Pan         = p.HasPan ? p.Pan : null,
        Tilt        = p.HasTilt ? p.Tilt : null,
        PresetToken = string.IsNullOrEmpty(p.PresetToken) ? null : p.PresetToken,
        PresetName  = string.IsNullOrEmpty(p.PresetName) ? null : p.PresetName,
        Residual    = residuals is not null && residuals.TryGetValue(i, out var r) ? r : null,
      }).ToList(),
    };
  }
}
