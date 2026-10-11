using Grpc.Core;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Vms.Cfg;
using Vms.PtzTracking;
using web_vms.Authentication;
using web_vms.Authorization;
using web_vms.Types.Dto;

namespace web_vms;

/// <summary>A fixed camera's picture mapped onto the ground, and the mappings a map needs to show detections.</summary>
/// <remarks>
/// The marks are configuration and live in VmsCfg; the fit is made by VmsPtzTrackingSingleton, as
/// the PTZ one is, and handed out as a matrix so the browser can project detections without it.
/// Marking a camera belongs to administrators, like placing it. The mappings for a map are read by
/// operators and filtered as placements are: a camera the operator may not watch shows nothing.
/// </remarks>
[ApiController]
[Route("api/ground-geo")]
public class GroundGeoController(
  CfgService.CfgServiceClient _cfg,
  PtzTrackingService.PtzTrackingServiceClient _tracking,
  ICameraAccessService _access) : ControllerBase
{
  private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

  private static CallOptions Options(CancellationToken ct) =>
    new(deadline: DateTime.UtcNow.Add(Timeout), cancellationToken: ct);

  [HttpGet]
  [CameraAccess(CameraAction.ViewLive, Source = CameraIdSource.Action)]
  public async Task<ActionResult<IReadOnlyList<GroundProjectionDto>>> List([FromQuery] string spaceId, CancellationToken ct)
  {
    var response = await _tracking.ListGroundProjectionsAsync(
      new ListGroundProjectionsRequest { SpaceId = spaceId }, Options(ct));
    var scope = await _access.ScopeAsync(User, CameraAction.ViewLive, ct);
    return scope.Filter(response.Projections, p => p.CameraId)
      .Select(p => new GroundProjectionDto { CameraId = p.CameraId, Matrix = p.Matrix.ToList() })
      .ToList();
  }

  [HttpGet("{cameraId}")]
  [Authorize(Policy = AuthPolicies.AdminOnly)]
  public async Task<ActionResult<GroundCalibrationDto>> Get(string cameraId, CancellationToken ct)
  {
    var stored = await _cfg.GetGroundObservationsAsync(new SourceRequest { SourceId = cameraId }, Options(ct));

    // As with the PTZ fit: the tracking service being down does not make the marks unworthy of
    // showing and editing.
    GetGroundFitResponse? fit = null;
    try
    {
      fit = await _tracking.GetGroundFitAsync(new GetGroundFitRequest { CameraId = cameraId }, Options(ct));
    }
    catch (RpcException) { }

    var residuals = fit?.Residuals.ToDictionary(r => r.LandmarkId) ?? [];
    return new GroundCalibrationDto
    {
      Observations = stored.Observations.Select(o => new GroundObservationDto
      {
        LandmarkId = o.LandmarkId,
        X = o.X,
        Y = o.Y,
        ResidualM = residuals.TryGetValue(o.LandmarkId, out var r) && r.HasDistanceM ? r.DistanceM : null,
      }).ToList(),
      Fitted = fit is null ? null : fit.Fitted,
      Error = fit is null
        ? null
        : !fit.Found ? "The camera is not placed on a map"
        : string.IsNullOrEmpty(fit.Error) ? null : fit.Error,
      Matrix = fit is { Fitted: true } ? fit.Matrix.ToList() : null,
    };
  }

  /// Replaces the camera's marks whole.
  [HttpPut("{cameraId}")]
  [Authorize(Policy = AuthPolicies.AdminOnly)]
  public async Task<IActionResult> Put(string cameraId, [FromBody] GroundCalibrationDto dto, CancellationToken ct)
  {
    var request = new GroundObservations { CameraId = cameraId };
    request.Observations.AddRange(dto.Observations.Select(o => new GroundObservation
    {
      LandmarkId = o.LandmarkId,
      X = o.X,
      Y = o.Y,
    }));
    return SpatialController.Result(await _cfg.SetGroundObservationsAsync(request, Options(ct)));
  }
}
