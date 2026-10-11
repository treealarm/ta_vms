using Grpc.Core;
using Microsoft.AspNetCore.Mvc;
using Vms.Cfg;
using Vms.PtzTracking;
using web_vms.Authorization;
using web_vms.Types.Dto;

namespace web_vms;

/// <summary>A PTZ camera's calibration against landmarks, and aiming it at a place on the map.</summary>
/// <remarks>
/// Guarded by <see cref="CameraAction.Ptz"/>, the joystick's right: everything here ends in this
/// camera's motor turning. Aiming goes through VmsPtzTrackingSingleton, which owns the motor and
/// the fit; the observations themselves are configuration and live in VmsCfg.
/// </remarks>
[ApiController]
[Route("api/ptz-geo")]
public class PtzGeoController(
  CfgService.CfgServiceClient _cfg,
  PtzTrackingService.PtzTrackingServiceClient _tracking) : ControllerBase
{
  // Long enough to cover the camera travelling: the aim waits for the move to be accepted.
  private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

  private static CallOptions Options(CancellationToken ct) =>
    new(deadline: DateTime.UtcNow.Add(Timeout), cancellationToken: ct);

  [HttpGet("{cameraId}")]
  [CameraAccess(CameraAction.Ptz)]
  public async Task<ActionResult<PtzGeoCalibrationDto>> Get(string cameraId, CancellationToken ct)
  {
    var stored = await _cfg.GetPtzGeoObservationsAsync(new SourceRequest { SourceId = cameraId }, Options(ct));

    // The fit comes from the tracking service. Its being down is not the request failing: the
    // observations are still worth showing and editing without the numbers beside them.
    GetPtzGeoFitResponse? fit = null;
    try
    {
      fit = await _tracking.GetPtzGeoFitAsync(new GetPtzGeoFitRequest { CameraId = cameraId }, Options(ct));
    }
    catch (RpcException) { }

    var residuals = fit?.Residuals.ToDictionary(r => r.LandmarkId) ?? [];
    return new PtzGeoCalibrationDto
    {
      Observations = stored.Observations.Select(o =>
      {
        residuals.TryGetValue(o.LandmarkId, out var r);
        return new PtzGeoObservationDto
        {
          LandmarkId = o.LandmarkId,
          Pan = o.Pan,
          Tilt = o.Tilt,
          PanResidual = r is { HasPan: true } ? r.Pan : null,
          TiltResidual = r is { HasTilt: true } ? r.Tilt : null,
        };
      }).ToList(),
      Fitted = fit is null ? null : fit.Fitted,
      Error = fit is null
        ? null
        : !fit.Found ? "The camera is not placed on a map"
        : string.IsNullOrEmpty(fit.Error) ? null : fit.Error,
    };
  }

  /// Replaces the camera's observations whole.
  [HttpPut("{cameraId}")]
  [CameraAccess(CameraAction.Ptz)]
  public async Task<IActionResult> Put(string cameraId, [FromBody] PtzGeoCalibrationDto dto, CancellationToken ct)
  {
    var request = new PtzGeoObservations { CameraId = cameraId };
    request.Observations.AddRange(dto.Observations.Select(o => new PtzGeoObservation
    {
      LandmarkId = o.LandmarkId,
      Pan = o.Pan,
      Tilt = o.Tilt,
    }));
    return SpatialController.Result(await _cfg.SetPtzGeoObservationsAsync(request, Options(ct)));
  }

  [HttpPost("{cameraId}/aim")]
  [CameraAccess(CameraAction.Ptz)]
  public async Task<ActionResult<PtzGeoAimResultDto>> Aim(string cameraId, [FromBody] PtzGeoAimDto dto, CancellationToken ct)
  {
    var response = await _tracking.AimAtWorldPointAsync(new AimAtWorldPointRequest
    {
      CameraId = cameraId,
      SpaceId = dto.SpaceId,
      Lat = dto.Lat,
      Lon = dto.Lon,
      HeightM = dto.HeightM,
    }, Options(ct));

    if (!response.Success)
      return BadRequest(new { error = response.Error });

    return new PtzGeoAimResultDto
    {
      Pan = response.Pan,
      Tilt = response.Tilt,
      AzimuthDeg = response.AzimuthDeg,
      ElevationDeg = response.ElevationDeg,
      DistanceM = response.DistanceM,
      Extrapolated = response.Extrapolated,
    };
  }
}
