using Grpc.Core;
using Microsoft.AspNetCore.Mvc;
using Vms.Cfg;
using Vms.Onvif;
using web_srv.Authentication;
using web_srv.Authorization;
using MapProto = Vms.Map;

namespace web_srv;

[ApiController]
[Route("api/[controller]")]
public class DeviceIoController(
  OnvifRouter router,
  CfgService.CfgServiceClient cfgClient,
  MapProto.MapService.MapServiceClient map,
  IRightsService access) : ControllerBase
{
  private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

  // The kind a camera's parts are registered under in VmsMap's device_node registry. A fire panel
  // or another device hub is a kind of its own there, never a second spelling of this one.
  private const string CameraSource = "camera";
  private const string DigitalInputKind = "digital_input";

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

    // Shown so the panel can say why a switch does not move, instead of failing on the click.
    var canControl = await access.IsAllowedAsync(User, cameraId, RightActions.Control, ct);

    return Ok(response.Relays.Select(r => new
    {
      token   = r.Token,
      mode    = FormatMode(r.Mode),
      idleState = FormatIdleState(r.IdleState),
      delayMs = r.DelayMs,
      // null until the camera has said anything: "no event yet" is not "inactive". While a
      // command waits for the camera's confirmation this is the state that was asked for.
      liveActive = r.HasLiveActive ? r.LiveActive : (bool?)null,
      // The value above is the operator's wish, not the camera's report yet.
      livePending = r.Pending,
      canControl,
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

    // The mode and the rule are the map's; the effective arming is resolved there too. Both are
    // read tolerantly: the live list is what this endpoint is for, and a service that cannot
    // answer leaves its fields null rather than failing the read.
    var canControl = await access.IsAllowedAsync(User, cameraId, RightActions.Control, ct);

    var rows = new List<object>();
    foreach (var input in response.Inputs)
    {
      var settingsRow = await TryMapSettingsAsync(cameraId, DigitalInputKind, input.Token, ct);
      var (armed, armingSource) = await TryArmingAsync(cameraId, input.Token, ct);

      rows.Add(new
      {
        token = input.Token,
        idleState = input.HasIdleState ? FormatIdleState(input.IdleState) : (string?)null,
        // Logical state from the camera's property events; a digital input reports true/false where
        // a relay reports active/inactive. null means the camera has not reported one yet.
        liveActive = input.HasLiveActive ? input.LiveActive : (bool?)null,
        // The configured mode, and what it actually resolves to: 'inherit' is not "disarmed" — the
        // camera, or the zone the camera stands in, may have the sensor on guard. null when the
        // part has no row, or no node in the map yet.
        armMode = settingsRow?.ArmMode,
        alarmEnabled = settingsRow?.AlarmEnabled,
        armed,
        armingSource,
        canControl,
      });
    }

    return Ok(rows);
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

  // The operator's guard switch. It changes what the site does, so it is control like driving a
  // relay — and only the mode moves: the name, the trigger and the alarm flag stay the
  // administrator's, and 'inherit' is not something an operator can write.
  // A refusal is an RpcException from VmsCfg; RpcExceptionFilter turns it into 400/404/503/...
  [HttpPut("cameras/{cameraId}/digital-inputs/{token}/arm-mode")]
  [RequiresRight(RightActions.Control)]
  public async Task<IActionResult> SetDigitalInputArmMode(
    string cameraId, string token, [FromBody] SetArmModeBody body, CancellationToken ct)
  {
    // The mode is the map's row now; the map's facade takes all three and refuses a part with no
    // node. `inherit` is the operator handing the input back to the camera (and the zone) it
    // stands in.
    await map.SetDeviceArmingAsync(new MapProto.SetDeviceArmingRequest
    {
      Key = PartKey(cameraId, DigitalInputKind, token),
      Mode = body.ArmMode ?? string.Empty,
      Caller = User.ToMapCaller(),
    }, Options(ct));

    return Ok();
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
      // The bearing it says it looks along, a compass bearing; absent when it says nothing.
      headingDeg = response.HasHeadingDeg ? response.HeadingDeg : (double?)null,
    });
  }

  // ── The logical object's settings ─────────────────────────────────────────

  // The card a camera's setup page and a part's row show: the arming mode and, for a digital
  // input, the contact state that raises an alarm. Addressed by source and token because the form
  // holds the camera, not the part's node id; `partKind=device` with an empty token is the camera.
  [HttpGet("cameras/{cameraId}/device-settings")]
  [RequiresRight(RightActions.ViewLive)]
  public async Task<IActionResult> GetDeviceSettings(
    string cameraId, [FromQuery] string partKind, [FromQuery] string? partToken, CancellationToken ct)
  {
    var response = await map.GetDeviceSettingsAsync(new MapProto.GetDeviceSettingsRequest
    {
      Key = PartKey(cameraId, partKind, partToken ?? string.Empty),
    }, Options(ct));

    return response.Found ? Ok(ToDeviceSettingsDto(response.Settings)) : NotFound();
  }

  [HttpPut("cameras/{cameraId}/device-settings")]
  [RequiresRight(RightActions.Control)]
  public async Task<IActionResult> SetDeviceSettings(
    string cameraId, [FromQuery] string partKind, [FromQuery] string? partToken,
    [FromBody] DeviceSettingsRuleBody body, CancellationToken ct)
  {
    var response = await map.SetDeviceSettingsAsync(new MapProto.SetDeviceSettingsRequest
    {
      Key = PartKey(cameraId, partKind, partToken ?? string.Empty),
      ArmMode = body.ArmMode ?? string.Empty,
      TriggerOn = body.TriggerOn ?? string.Empty,
      AlarmEnabled = body.AlarmEnabled,
      Caller = User.ToMapCaller(),
    }, Options(ct));

    return response.Found ? Ok(ToDeviceSettingsDto(response.Settings)) : NotFound();
  }

  private static ObjectSettingsDto ToDeviceSettingsDto(MapProto.DeviceSettingsView settings) => new(
    settings.ObjectId,
    settings.Key?.SourceKind ?? string.Empty,
    settings.Key?.SourceId ?? string.Empty,
    settings.Key?.PartKind ?? string.Empty,
    settings.Key?.PartToken ?? string.Empty,
    settings.ArmMode,
    settings.ArmModes.ToList(),
    string.IsNullOrWhiteSpace(settings.TriggerOn) ? null : settings.TriggerOn,
    settings.TriggerModes.ToList(),
    settings.AlarmEnabled,
    settings.Configured,
    settings.Arming?.Armed ?? false,
    settings.Arming?.Source ?? string.Empty,
    settings.Arming?.SourceObjectId ?? string.Empty);

  // ── Per-part settings ─────────────────────────────────────────────────────

  // The administrator's configuration of a part: its name, trigger and alarm flag, and the mode it
  // follows. The operator's arm/disarm switch is separate (SetDigitalInputArmMode) and touches the
  // mode alone, so a card holding a stale copy cannot undo an alarm setting.
  [HttpGet("cameras/{cameraId}/settings")]
  [RequiresRight(RightActions.ViewLive)]
  public async Task<IActionResult> GetSettings(string cameraId, CancellationToken ct)
  {
    var response = await cfgClient.ListDeviceSettingsAsync(
      new ListDeviceSettingsRequest { CameraId = cameraId }, Options(ct));

    var rows = new List<DeviceSettingsDto>(response.Settings.Count);
    foreach (var s in response.Settings)
    {
      // The name is the device's identity and stays in VmsCfg; the mode and the alarm rule are the
      // map's, so a card never shows the legacy columns' stale values.
      var own = await TryMapSettingsAsync(cameraId, s.Kind, s.Token, ct);
      rows.Add(new DeviceSettingsDto(
        s.CameraId,
        s.Kind,
        s.Token,
        s.Name,
        own?.ArmMode ?? "inherit",
        string.IsNullOrWhiteSpace(own?.TriggerOn) ? null : own.TriggerOn,
        own?.AlarmEnabled ?? false));
    }

    return Ok(rows);
  }

  // Writing a part's arming or alarm is control: it changes what the site does, not what is shown.
  [HttpPut("cameras/{cameraId}/settings")]
  [RequiresRight(RightActions.Control)]
  public async Task<IActionResult> SetSettings(
    string cameraId, [FromBody] DeviceSettingsBody body, CancellationToken ct)
  {
    // The name is VmsCfg's row, and only the name: its arming and its rule are the map's, and
    // VmsCfg refuses a request that still carries them. Writing it first is also what makes the
    // part "configured", which the map's settings require.
    var response = await cfgClient.SetDeviceSettingsAsync(new SetDeviceSettingsRequest
    {
      Settings = new DeviceSettings
      {
        CameraId = cameraId,
        Kind = body.Kind,
        Token = body.Token,
        Name = body.Name ?? string.Empty,
      },
    }, Options(ct));
    if (!response.Success)
      return BadRequest(response.Message);

    if (body.Kind == DigitalInputKind)
    {
      await map.SetDeviceSettingsAsync(new MapProto.SetDeviceSettingsRequest
      {
        Key = PartKey(cameraId, DigitalInputKind, body.Token),
        ArmMode = body.ArmMode,
        TriggerOn = body.TriggerOn ?? string.Empty,
        AlarmEnabled = body.AlarmEnabled,
        Caller = User.ToMapCaller(),
      }, Options(ct));
    }

    return Ok();
  }

  /// <summary>The map's own settings for one part, or null when VmsMap cannot answer.</summary>
  private async Task<MapProto.DeviceSettingsView?> TryMapSettingsAsync(
    string cameraId, string kind, string token, CancellationToken ct)
  {
    try
    {
      var response = await map.GetDeviceSettingsAsync(new MapProto.GetDeviceSettingsRequest
      {
        Key = PartKey(cameraId, kind, token),
      }, Options(ct));
      return response.Found ? response.Settings : null;
    }
    catch (RpcException)
    {
      return null;
    }
  }

  private static MapProto.DeviceSettingsKey PartKey(string cameraId, string kind, string token) => new()
  {
    SourceKind = CameraSource,
    SourceId = cameraId,
    PartKind = kind,
    PartToken = token,
  };


  // The effective arming of one part, as VmsMap resolves it. Addressed by source and token because
  // the card holds the camera, not the part's node id. An unanswered call is two nulls: the panel
  // says the state is unknown rather than that the sensor is off guard.
  private async Task<(bool? Armed, string? Source)> TryArmingAsync(
    string cameraId, string token, CancellationToken ct)
  {
    try
    {
      var response = await map.GetDevicePartArmingAsync(new MapProto.GetDevicePartArmingRequest
      {
        SourceKind = CameraSource,
        SourceId = cameraId,
        PartKind = DigitalInputKind,
        PartToken = token,
      }, Options(ct));

      return response.Found ? (response.Arming.Armed, response.Arming.Source) : (null, null);
    }
    catch (RpcException)
    {
      return (null, null);
    }
  }

  private static string FormatMode(RelayMode mode) => mode == RelayMode.Bistable ? "bistable" : "monostable";
  private static RelayMode ParseMode(string mode) =>
    mode.Equals("bistable", StringComparison.OrdinalIgnoreCase) ? RelayMode.Bistable : RelayMode.Monostable;

  private static string FormatIdleState(IdleState idleState) => idleState == IdleState.Open ? "open" : "closed";
  private static IdleState ParseIdleState(string idleState) =>
    idleState.Equals("open", StringComparison.OrdinalIgnoreCase) ? IdleState.Open : IdleState.Closed;
}

public record DeviceSettingsDto(
  string CameraId,
  string Kind,
  string Token,
  string Name,
  string ArmMode,
  string? TriggerOn,
  bool AlarmEnabled);

public record DeviceSettingsBody(
  string Kind,
  string Token,
  string? Name,
  string ArmMode,
  string? TriggerOn,
  bool AlarmEnabled);

public record SetRelayStateBody(bool Active);
public record SetRelaySettingsBody(string Mode, string IdleState, int DelayMs);
public record SetIdleStateBody(string IdleState);
public record SetArmModeBody(string? ArmMode);
public record DeviceSettingsRuleBody(string? ArmMode, string? TriggerOn, bool AlarmEnabled);
