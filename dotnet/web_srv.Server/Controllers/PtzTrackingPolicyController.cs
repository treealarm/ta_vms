using Microsoft.AspNetCore.Mvc;
using Vms.Cfg;
using web_srv.Authorization;
using web_srv.Types.Dto;

namespace web_srv;

/// <summary>
/// Whether a camera follows objects on its own, and which ones it prefers.
/// </summary>
/// <remarks>
/// Configuration, not a control: switching this on makes the camera move by itself, around the
/// clock, whether or not anybody is watching a monitor. That is why it lives with the camera's
/// settings and survives a restart, while pointing a camera at one particular object — a temporary
/// thing an operator does — lives on PtzTrackingController.
/// <para>
/// Guarded by <see cref="CameraAction.Ptz"/>, the same right the joystick and the tracking override
/// carry: all three end in the camera's motor turning.
/// </para>
/// </remarks>
[ApiController]
[Route("api/ptz-tracking-policy")]
public class PtzTrackingPolicyController(CfgService.CfgServiceClient _cfg) : ControllerBase
{
  [HttpGet("{cameraId}")]
  [CameraAccess(CameraAction.Ptz)]
  public async Task<IActionResult> Get(string cameraId, CancellationToken ct)
  {
    var response = await _cfg.GetPtzTrackingPolicyAsync(
      new GetPtzTrackingPolicyRequest { CameraId = cameraId }, cancellationToken: ct);

    // A camera with no row is not an error, it is a camera that does not guide itself. Answering
    // 404 would make the settings page treat "off" as "could not load".
    if (!response.Found)
      return Ok(new PtzTrackingPolicyDto());

    return Ok(new PtzTrackingPolicyDto
    {
      Enabled = response.Policy.Enabled,
      Kinds = response.Policy.Kinds.ToList(),
      Sources = response.Policy.Sources.ToList(),
      MasterCameraId = response.Policy.HasMasterCameraId && response.Policy.MasterCameraId.Length > 0
        ? response.Policy.MasterCameraId
        : null,
    });
  }

  /// <summary>
  /// Switches self-guidance on or off, leaving what the camera follows alone.
  /// </summary>
  /// <remarks>
  /// The monitor's button ends up here. It is a separate route rather than a flag on the full PUT
  /// because the monitor does not know the camera's priorities and must not be able to erase them
  /// by not sending them: an upsert replaces the row. Read-modify-write rather than a partial
  /// update in VmsCfg, because a whole-row upsert is the only shape the config service offers and
  /// two operators racing over one camera's on/off switch is not a problem worth a contract for.
  /// </remarks>
  [HttpPut("{cameraId}/enabled")]
  [CameraAccess(CameraAction.Ptz)]
  public async Task<IActionResult> SetEnabled(
    string cameraId, [FromBody] PtzTrackingEnabledDto dto, CancellationToken ct)
  {
    var current = await _cfg.GetPtzTrackingPolicyAsync(
      new GetPtzTrackingPolicyRequest { CameraId = cameraId }, cancellationToken: ct);

    var policy = new PtzTrackingPolicy { CameraId = cameraId, Enabled = dto.Enabled };
    if (current.Found)
    {
      policy.Kinds.AddRange(current.Policy.Kinds);
      policy.Sources.AddRange(current.Policy.Sources);
    }

    var status = await _cfg.UpsertPtzTrackingPolicyAsync(
      new UpsertPtzTrackingPolicyRequest { Policy = policy }, cancellationToken: ct);

    return status.Success ? Ok() : BadRequest(status.Message);
  }

  [HttpPut("{cameraId}")]
  [CameraAccess(CameraAction.Ptz)]
  public async Task<IActionResult> Put(
    string cameraId, [FromBody] PtzTrackingPolicyDto dto, CancellationToken ct)
  {
    var policy = new PtzTrackingPolicy { CameraId = cameraId, Enabled = dto.Enabled };
    policy.Kinds.AddRange(dto.Kinds);
    policy.Sources.AddRange(dto.Sources);
    // master_camera_id is deliberately not written here. Which master a camera follows is decided
    // where the survey is marked out (PtzSlaveBindingController), so that it is named in one place
    // only; VmsCfg leaves the stored value alone when this field is unset.

    var status = await _cfg.UpsertPtzTrackingPolicyAsync(
      new UpsertPtzTrackingPolicyRequest { Policy = policy }, cancellationToken: ct);

    return status.Success ? Ok() : BadRequest(status.Message);
  }
}
