using Grpc.Core;
using Microsoft.AspNetCore.Mvc;
using Vms.Cfg;
using web_srv.Authorization;

namespace web_srv;

[ApiController]
[Route("api/cameras")]
public class CameraStateController(CfgService.CfgServiceClient cfgClient) : ControllerBase
{
  private CallOptions Options(CancellationToken ct) =>
    new(deadline: DateTime.UtcNow.AddSeconds(5), cancellationToken: ct);

  [HttpGet("{cameraId}/state")]
  [RequiresRight(RightActions.ViewLive)]
  public async Task<IActionResult> GetState(string cameraId, CancellationToken ct)
  {
    var resp = await cfgClient.GetCamStateAsync(
      new GetCamStateRequest { CameraId = cameraId }, Options(ct));
    return Ok(new CamStateDto(resp.Armed, resp.ArmMode));
  }

  // Arming is a control action, not a viewing one. An operator's command: the camera is armed or
  // not, and that is an explicit decision the zone it stands in no longer overrides.
  [HttpPut("{cameraId}/state")]
  [RequiresRight(RightActions.Control)]
  public async Task<IActionResult> SetState(string cameraId, [FromBody] CamStateDto body, CancellationToken ct)
  {
    await cfgClient.SetCamStateAsync(
      new SetCamStateRequest { CameraId = cameraId, Armed = body.Armed }, Options(ct));
    return Ok();
  }

  // The administrator's setting: `inherit` hands the flag back to the zone the camera stands in.
  [HttpPut("{cameraId}/arm-mode")]
  [RequiresRight(RightActions.Control)]
  public async Task<IActionResult> SetArmMode(string cameraId, [FromBody] CamArmModeDto body, CancellationToken ct)
  {
    await cfgClient.SetCamArmModeAsync(
      new SetCamArmModeRequest { CameraId = cameraId, ArmMode = body.ArmMode ?? string.Empty }, Options(ct));
    return Ok();
  }
}

/// <summary>`ArmMode` is empty on a write that only says armed or not: the operator's command.</summary>
public record CamStateDto(bool Armed, string? ArmMode = null);

public record CamArmModeDto(string? ArmMode);
