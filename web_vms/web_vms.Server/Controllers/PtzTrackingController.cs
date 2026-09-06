using Grpc.Core;
using Microsoft.AspNetCore.Mvc;
using Vms.PtzTracking;
using web_vms.Authorization;
using web_vms.Types.Dto;

namespace web_vms;

/// <summary>
/// Taking over a self-guiding camera, and asking what every camera is following.
/// </summary>
/// <remarks>
/// The loop itself lives in VmsPtzTrackingSingleton, which reads detections and drives the camera
/// on its own; nothing here is on that path. Driving a camera is driving a camera whether a person
/// or an algorithm decides where, so these carry the same <see cref="CameraAction.Ptz"/> right the
/// joystick does.
/// <para>
/// Whether a camera guides itself at all is not set here — that is configuration, and it lives on
/// PtzTrackingPolicyController.
/// </para>
/// </remarks>
[ApiController]
[Route("api/ptz-tracking")]
public class PtzTrackingController(
  PtzTrackingService.PtzTrackingServiceClient _tracking,
  ICameraAccessService _access) : ControllerBase
{
  private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

  private static CallOptions Options(CancellationToken ct) =>
    new(deadline: DateTime.UtcNow.Add(Timeout), cancellationToken: ct);

  [HttpPut("{cameraId}")]
  [CameraAccess(CameraAction.Ptz)]
  public async Task<IActionResult> SetTarget(
    string cameraId, [FromBody] SetPtzTargetDto dto, CancellationToken ct)
  {
    var request = new SetTargetRequest { CameraId = cameraId };
    if (dto.Target is not null)
      request.Target = new TrackingTarget
      {
        Source = dto.Target.Source,
        TrackId = dto.Target.TrackId,
        Kind = AnalyticsKindMapper.FromStringId(dto.Target.Kind),
      };

    var response = await _tracking.SetTargetAsync(request, Options(ct));
    return response.Success ? Ok() : BadRequest(response.Error);
  }

  // Reading which cameras are tracking drives the monitor's layout, so it follows viewing rights —
  // the same split PtzController makes between moving a camera and seeing what it can do.
  [HttpGet]
  [CameraAccess(CameraAction.ViewLive, Source = CameraIdSource.Action)]
  public async Task<IActionResult> ListStates(CancellationToken ct)
  {
    var response = await _tracking.ListTrackingStatesAsync(new ListTrackingStatesRequest(), Options(ct));
    var scope = await _access.ScopeAsync(User, CameraAction.ViewLive, ct);

    return Ok(scope.Filter(response.States, s => s.CameraId).Select(s => new PtzTrackingStateDto
    {
      CameraId = s.CameraId,
      Active = s.Active,
      Target = s.Target is null
        ? null
        : new TrackingTargetDto(s.Target.Source, s.Target.TrackId, AnalyticsKindMapper.ToStringId(s.Target.Kind)),
      ExplicitTarget = s.ExplicitTarget,
      TargetSeenAt = s.TargetSeenAt?.ToDateTime(),
      LastError = s.LastError,
    }));
  }
}
