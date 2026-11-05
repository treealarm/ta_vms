using Grpc.Core;
using Microsoft.AspNetCore.Mvc;
using Vms.Onvif;
using web_srv.Authorization;

namespace web_srv;

[ApiController]
[Route("api/[controller]")]
public class DeviceIoController(OnvifRouter router) : ControllerBase
{
  private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

  private static CallOptions Options(CancellationToken ct) =>
    new(deadline: DateTime.UtcNow.Add(Timeout), cancellationToken: ct);

  [HttpGet("cameras/{cameraId}/relay-outputs")]
  [RequiresRight(RightActions.ViewLive)]
  public async Task<IActionResult> GetRelayOutputs(string cameraId, CancellationToken ct)
  {
    var response = await (await router.DeviceIoAsync(cameraId, ct)).GetRelayOutputsAsync(
      new CameraRequest { SourceId = cameraId }, Options(ct));

    if (!string.IsNullOrEmpty(response.Error))
      return BadRequest(response.Error);

    return Ok(response.Relays.Select(r => new
    {
      token   = r.Token,
      mode    = FormatMode(r.Mode),
      idleState = FormatIdleState(r.IdleState),
      delayMs = r.DelayMs,
      // null until the camera has said anything: "no event yet" is not "inactive".
      liveActive = r.HasLiveActive ? r.LiveActive : (bool?)null,
    }));
  }

  [HttpGet("cameras/{cameraId}/relay-outputs/options")]
  [RequiresRight(RightActions.ViewLive)]
  public async Task<IActionResult> GetRelayOutputOptions(string cameraId, CancellationToken ct)
  {
    var response = await (await router.DeviceIoAsync(cameraId, ct)).GetRelayOutputOptionsAsync(
      new CameraRequest { SourceId = cameraId }, Options(ct));

    if (!string.IsNullOrEmpty(response.Error))
      return BadRequest(response.Error);

    return Ok(response.Options.Select(o => new
    {
      token             = o.Token,
      supportedModes    = o.SupportedModes.Select(FormatMode),
      discrete          = o.Discrete,
      minDelayMs        = o.MinDelayMs,
      maxDelayMs        = o.MaxDelayMs,
      discreteDelaysMs  = o.DiscreteDelaysMs,
    }));
  }

  // Driving a relay acts on the site itself — control, not viewing.
  [HttpPost("cameras/{cameraId}/relay-outputs/{token}/state")]
  [RequiresRight(RightActions.Control)]
  public async Task<IActionResult> SetRelayState(
    string cameraId, string token, [FromBody] SetRelayStateBody body, CancellationToken ct)
  {
    var response = await (await router.DeviceIoAsync(cameraId, ct)).SetRelayOutputStateAsync(
      new SetRelayOutputStateRequest { SourceId = cameraId, Token = token, Active = body.Active },
      Options(ct));

    return response.Success ? Ok() : BadRequest(response.Error);
  }

  [HttpPost("cameras/{cameraId}/relay-outputs/{token}/settings")]
  [RequiresRight(RightActions.Control)]
  public async Task<IActionResult> SetRelaySettings(
    string cameraId, string token, [FromBody] SetRelaySettingsBody body, CancellationToken ct)
  {
    var response = await (await router.DeviceIoAsync(cameraId, ct)).SetRelayOutputSettingsAsync(
      new SetRelayOutputSettingsRequest
      {
        SourceId  = cameraId,
        Token     = token,
        Mode      = ParseMode(body.Mode),
        IdleState = ParseIdleState(body.IdleState),
        DelayMs   = body.DelayMs,
      }, Options(ct));

    return response.Success ? Ok() : BadRequest(response.Error);
  }

  [HttpGet("cameras/{cameraId}/digital-inputs")]
  [RequiresRight(RightActions.ViewLive)]
  public async Task<IActionResult> GetDigitalInputs(string cameraId, CancellationToken ct)
  {
    var response = await (await router.DeviceIoAsync(cameraId, ct)).GetDigitalInputsAsync(
      new CameraRequest { SourceId = cameraId }, Options(ct));

    if (!string.IsNullOrEmpty(response.Error))
      return BadRequest(response.Error);

    return Ok(response.Inputs.Select(i => new
    {
      token     = i.Token,
      idleState = i.HasIdleState ? FormatIdleState(i.IdleState) : (string?)null,
      // Logical state from the camera's property events; a digital input reports true/false where a
      // relay reports active/inactive. null means the camera has not reported one yet.
      liveActive = i.HasLiveActive ? i.LiveActive : (bool?)null,
    }));
  }

  [HttpGet("cameras/{cameraId}/digital-inputs/options")]
  [RequiresRight(RightActions.ViewLive)]
  public async Task<IActionResult> GetDigitalInputOptions(string cameraId, CancellationToken ct)
  {
    var response = await (await router.DeviceIoAsync(cameraId, ct)).GetDigitalInputOptionsAsync(
      new CameraRequest { SourceId = cameraId }, Options(ct));

    if (!string.IsNullOrEmpty(response.Error))
      return BadRequest(response.Error);

    return Ok(response.Options.Select(o => new
    {
      token                 = o.Token,
      idleStateConfigurable = o.IdleStateConfigurable,
    }));
  }

  [HttpPost("cameras/{cameraId}/digital-inputs/{token}/idle-state")]
  [RequiresRight(RightActions.Control)]
  public async Task<IActionResult> SetDigitalInputIdleState(
    string cameraId, string token, [FromBody] SetIdleStateBody body, CancellationToken ct)
  {
    var response = await (await router.DeviceIoAsync(cameraId, ct)).SetDigitalInputIdleStateAsync(
      new SetDigitalInputIdleStateRequest { SourceId = cameraId, Token = token, IdleState = ParseIdleState(body.IdleState) },
      Options(ct));

    return response.Success ? Ok() : BadRequest(response.Error);
  }

  // Where the camera itself says it stands. Read-only: a position on the map is the
  // administrator's, and only a camera flagged to follow the device takes this one into account.
  // No location is a normal answer — many cameras never report one — so it is found=false, not 404.
  [HttpGet("cameras/{cameraId}/geo-location")]
  [RequiresRight(RightActions.ViewLive)]
  public async Task<IActionResult> GetGeoLocation(string cameraId, CancellationToken ct)
  {
    var response = await (await router.DeviceIoAsync(cameraId, ct)).GetCameraGeoLocationAsync(
      new CameraRequest { SourceId = cameraId }, Options(ct));

    if (!string.IsNullOrEmpty(response.Error))
      return BadRequest(response.Error);

    return Ok(new
    {
      found = response.Found,
      // A coordinate the camera did not send stays null: an absent latitude is not the equator.
      lat = response.HasLat ? response.Lat : (double?)null,
      lon = response.HasLon ? response.Lon : (double?)null,
      elevationM = response.HasElevationM ? response.ElevationM : (double?)null,
    });
  }

  private static string FormatMode(RelayMode mode) => mode == RelayMode.Bistable ? "bistable" : "monostable";
  private static RelayMode ParseMode(string mode) =>
    mode.Equals("bistable", StringComparison.OrdinalIgnoreCase) ? RelayMode.Bistable : RelayMode.Monostable;

  private static string FormatIdleState(IdleState idleState) => idleState == IdleState.Open ? "open" : "closed";
  private static IdleState ParseIdleState(string idleState) =>
    idleState.Equals("open", StringComparison.OrdinalIgnoreCase) ? IdleState.Open : IdleState.Closed;
}

public record SetRelayStateBody(bool Active);
public record SetRelaySettingsBody(string Mode, string IdleState, int DelayMs);
public record SetIdleStateBody(string IdleState);
