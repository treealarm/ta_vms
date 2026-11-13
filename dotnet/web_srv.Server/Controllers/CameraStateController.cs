using Grpc.Core;
using Microsoft.AspNetCore.Mvc;
using Vms.Cfg;
using web_srv.Authentication;
using web_srv.Authorization;
using MapProto = Vms.Map;

namespace web_srv;

[ApiController]
[Route("api/cameras")]
public class CameraStateController(
  CfgService.CfgServiceClient cfgClient,
  MapProto.MapService.MapServiceClient map) : ControllerBase
{
  private CallOptions Options(CancellationToken ct) =>
    new(deadline: DateTime.UtcNow.AddSeconds(5), cancellationToken: ct);

  // The card's own poll. The flag is what VmsOnvif and the live state read; the camera's mode is
  // the map's and is read by the admin card from its settings endpoint, not on every poll.
  [HttpGet("{cameraId}/state")]
  [RequiresRight(RightActions.ViewLive)]
  public async Task<IActionResult> GetState(string cameraId, CancellationToken ct)
  {
    var resp = await cfgClient.GetCamStateAsync(
      new GetCamStateRequest { CameraId = cameraId }, Options(ct));
    return Ok(new CamStateDto(resp.Armed));
  }

  // Arming is a control action, not a viewing one. An operator's command: the camera is armed or
  // not, and that is an explicit decision the zone it stands in no longer overrides. It goes
  // through the map's facade, because the mode is the map's row now.
  [HttpPut("{cameraId}/state")]
  [RequiresRight(RightActions.Control)]
  public async Task<IActionResult> SetState(string cameraId, [FromBody] CamStateDto body, CancellationToken ct)
  {
    await map.SetDeviceArmingAsync(
      new MapProto.SetDeviceArmingRequest
      {
        Key = CameraKey(cameraId),
        Mode = body.Armed ? "armed" : "disarmed",
        Caller = User.ToMapCaller(),
      },
      MapApi.Options(ct));
    return Ok();
  }

  // The administrator's setting: `inherit` hands the flag back to the zone the camera stands in.
  [HttpPut("{cameraId}/arm-mode")]
  [RequiresRight(RightActions.Control)]
  public async Task<IActionResult> SetArmMode(string cameraId, [FromBody] CamArmModeDto body, CancellationToken ct)
  {
    await map.SetDeviceSettingsAsync(
      new MapProto.SetDeviceSettingsRequest
      {
        Key = CameraKey(cameraId),
        ArmMode = body.ArmMode ?? string.Empty,
        Caller = User.ToMapCaller(),
      },
      MapApi.Options(ct));
    return Ok();
  }

  private static MapProto.DeviceSettingsKey CameraKey(string cameraId) => new()
  {
    SourceKind = "camera",
    SourceId = cameraId,
    PartKind = "device",
    PartToken = string.Empty,
  };
}

/// <summary>
/// The card's flag, and the operator's command. The mode behind it is not here: it is the map's
/// row, read by the administrator's card from the settings endpoint rather than on every poll.
/// </summary>
public record CamStateDto(bool Armed);

public record CamArmModeDto(string? ArmMode);
