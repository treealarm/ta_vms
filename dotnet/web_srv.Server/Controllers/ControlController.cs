using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using web_srv.Authentication;
using web_srv.Authorization;
using MapProto = Vms.Map;

namespace web_srv;

// The operator's Control panel. The list is one page of the objects the operator can act on — the
// camera, its digital inputs and its relays — already filtered by the caller's `view_live` and with
// the arming and the live state resolved; VmsMap owns all of it, so nothing here decides what a
// user may see. The actions are not here: they are the commands that already write —
// PUT api/cameras/{id}/state, PUT api/DeviceIo/cameras/{id}/digital-inputs/{token}/arm-mode and
// POST api/DeviceIo/cameras/{id}/relay-outputs/{token}/state.
[ApiController]
[Authorize(Policy = AuthPolicies.Authenticated)]
[Route("api/control")]
public class ControlController(MapProto.MapService.MapServiceClient map) : ControllerBase
{
  [HttpGet("objects")]
  // Source = Action: the list filters itself in VmsMap, like the map reads. The filter only
  // records that a decision was made.
  [RequiresRight(RightActions.ViewLive, Source = RightSource.Action)]
  public async Task<IActionResult> ListObjects(
    [FromQuery] string? spaceId,
    [FromQuery] string? kind,
    [FromQuery] string? cameraId,
    [FromQuery] int pageSize,
    [FromQuery] string? afterName,
    [FromQuery] string? afterId,
    CancellationToken ct)
  {
    var request = new MapProto.ListControlObjectsRequest
    {
      SpaceId = spaceId ?? string.Empty,
      Kind = kind ?? string.Empty,
      CameraId = cameraId ?? string.Empty,
      PageSize = pageSize,
      Caller = User.ToMapCaller(),
    };

    // Half a cursor is a caller's mistake, not a position: VmsMap refuses it as InvalidArgument,
    // and half of it would silently restart from the beginning.
    if (!string.IsNullOrWhiteSpace(afterName) || !string.IsNullOrWhiteSpace(afterId))
    {
      request.After = new MapProto.ControlObjectCursor
      {
        AfterName = afterName ?? string.Empty,
        AfterId = afterId ?? string.Empty,
      };
    }

    var response = await map.ListControlObjectsAsync(request, MapApi.Options(ct));

    return Ok(new ControlObjectPageDto(
      response.Objects.Select(ToDto).ToList(),
      response.HasMore,
      response.HasMore && response.Next is not null
        ? new ControlObjectCursorDto(response.Next.AfterName, response.Next.AfterId)
        : null));
  }

  [HttpGet("objects/{objectId}")]
  [RequiresRight(RightActions.ViewLive, Source = RightSource.Action)]
  public async Task<IActionResult> GetObject(string objectId, CancellationToken ct)
  {
    var response = await map.GetControlObjectAsync(
      new MapProto.GetControlObjectRequest
      {
        ObjectId = objectId,
        Caller = User.ToMapCaller(),
      },
      MapApi.Options(ct));

    // Not found and not permitted are the same answer by design: what a caller may not see does
    // not exist for it.
    return response.Found && response.Object is not null
      ? Ok(ToDto(response.Object))
      : NotFound();
  }

  // The one guard switch, for a sensor, a camera or a container. The node id comes from the map
  // (the inspector draws a replica, and a replica resolves through its owner), so the filter
  // checks the same id the caller was given.
  [HttpGet("objects/{objectId}/arming")]
  [RequiresRight(RightActions.View, Name = "objectId")]
  public async Task<IActionResult> GetArming(string objectId, CancellationToken ct)
  {
    var response = await map.GetNodeArmingAsync(
      new MapProto.GetNodeArmingRequest { ObjectId = objectId },
      MapApi.Options(ct));

    return response.Found ? Ok(ToArmingDto(response)) : NotFound();
  }

  [HttpPut("objects/{objectId}/arming")]
  [RequiresRight(RightActions.Control, Name = "objectId")]
  public async Task<IActionResult> SetArming(
    string objectId, [FromBody] SetArmingDto body, CancellationToken ct)
  {
    // The answer is what the node resolves to afterwards: with inheritance the browser cannot
    // compute it from the mode it sent, and must not try.
    var response = await map.SetNodeArmingAsync(
      new MapProto.SetNodeArmingRequest
      {
        ObjectId = objectId,
        Mode = body.Mode ?? string.Empty,
        Caller = User.ToMapCaller(),
      },
      MapApi.Options(ct));

    return response.Found ? Ok(ToArmingDto(response)) : NotFound();
  }

  // Accepting a zone: every open alarm of the devices under it, one pass. A refusal is per alarm,
  // so the answer is a count and the reasons rather than one status for the whole operation.
  [HttpPost("objects/{objectId}/alarms/acknowledge")]
  [RequiresRight(RightActions.Acknowledge, Name = "objectId")]
  public async Task<IActionResult> AcknowledgeAlarms(
    string objectId, [FromBody] AcknowledgeZoneDto body, CancellationToken ct)
  {
    var response = await map.AcknowledgeSubtreeAlarmsAsync(
      new MapProto.AcknowledgeSubtreeAlarmsRequest
      {
        ObjectId = objectId,
        Comment = body.Comment ?? string.Empty,
        Caller = User.ToMapCaller(),
      },
      MapApi.Options(ct));

    return Ok(new AcknowledgeZoneResultDto(
      response.Acknowledged,
      response.Rejected.Select(r => new AlarmRejectionDto(r.AlarmId, r.Reason)).ToList()));
  }

  // The settings of the logical object a node stands for: its arming mode and, for a digital
  // input, the contact state that raises an alarm. Reading is a view of the node; writing is
  // control, as every change of what the site does.
  [HttpGet("objects/{objectId}/settings")]
  [RequiresRight(RightActions.View, Name = "objectId")]
  public async Task<IActionResult> GetSettings(string objectId, CancellationToken ct)
  {
    var key = await DeviceKeyAsync(objectId, ct);
    if (key is null)
      return NotFound();

    var response = await map.GetDeviceSettingsAsync(
      new MapProto.GetDeviceSettingsRequest { Key = key }, MapApi.Options(ct));

    return response.Found ? Ok(ToSettingsDto(response.Settings)) : NotFound();
  }

  [HttpPut("objects/{objectId}/settings")]
  [RequiresRight(RightActions.Control, Name = "objectId")]
  public async Task<IActionResult> SetSettings(
    string objectId, [FromBody] ObjectSettingsBody body, CancellationToken ct)
  {
    var key = await DeviceKeyAsync(objectId, ct);
    if (key is null)
      return NotFound();

    var response = await map.SetDeviceSettingsAsync(
      new MapProto.SetDeviceSettingsRequest
      {
        Key = key,
        ArmMode = body.ArmMode ?? string.Empty,
        TriggerOn = body.TriggerOn ?? string.Empty,
        AlarmEnabled = body.AlarmEnabled,
        Caller = User.ToMapCaller(),
      },
      MapApi.Options(ct));

    return response.Found ? Ok(ToSettingsDto(response.Settings)) : NotFound();
  }

  // A replica resolves to the device it shows, so a picture on a plan and the canonical node
  // answer the same settings.
  private async Task<MapProto.DeviceSettingsKey?> DeviceKeyAsync(string objectId, CancellationToken ct)
  {
    var row = await map.GetControlObjectAsync(
      new MapProto.GetControlObjectRequest { ObjectId = objectId, Caller = User.ToMapCaller() },
      MapApi.Options(ct));

    if (!row.Found || row.Object is not { } device || string.IsNullOrEmpty(device.SourceId))
      return null;

    return new MapProto.DeviceSettingsKey
    {
      SourceKind = device.SourceKind,
      SourceId = device.SourceId,
      PartKind = device.PartKind,
      PartToken = device.PartToken,
    };
  }

  private static ObjectSettingsDto ToSettingsDto(MapProto.DeviceSettingsView settings) => new(
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

  private static NodeArmingDto ToArmingDto(MapProto.GetNodeArmingResponse response) => new(
    response.Arming?.ObjectId ?? string.Empty,
    response.Armable,
    response.Configured,
    response.Mode,
    response.Arming?.Armed ?? false,
    response.Arming?.Source ?? string.Empty,
    response.Arming?.SourceObjectId ?? string.Empty,
    response.Modes.ToList());

  private static ControlObjectDto ToDto(MapProto.ControlObject row) => new(
    row.ObjectId,
    row.Kind,
    row.SourceKind,
    row.SourceId,
    row.PartKind,
    row.PartToken,
    row.Name,
    row.CameraName,
    row.SpaceId,
    row.SpaceName,
    row.State,
    row.ArmMode,
    row.AlarmEnabled,
    row.Configured,
    row.HasArming,
    row.Armed,
    row.ArmingSource,
    row.ArmingSourceObjectId,
    row.CanControl);
}

public record ControlObjectDto(
  string ObjectId,
  string Kind,
  string SourceKind,
  string SourceId,
  string PartKind,
  string PartToken,
  string Name,
  string CameraName,
  string SpaceId,
  string SpaceName,
  string State,
  string ArmMode,
  bool AlarmEnabled,
  bool Configured,
  bool HasArming,
  bool Armed,
  string ArmingSource,
  /// <summary>The node whose arming row settled it, when a zone did; empty otherwise.</summary>
  string ArmingSourceObjectId,
  bool CanControl);

public record ControlObjectPageDto(
  IReadOnlyList<ControlObjectDto> Objects,
  bool HasMore,
  ControlObjectCursorDto? Next);

/// <summary>One logical device object's settings, as the administrator's card shows them.</summary>
public record ObjectSettingsDto(
  string ObjectId,
  string SourceKind,
  string SourceId,
  string PartKind,
  string PartToken,
  string ArmMode,
  IReadOnlyList<string> ArmModes,
  string? TriggerOn,
  IReadOnlyList<string> TriggerModes,
  bool AlarmEnabled,
  bool Configured,
  bool Armed,
  string ArmingSource,
  string ArmingSourceObjectId);

public record ObjectSettingsBody(string? ArmMode, string? TriggerOn, bool AlarmEnabled);

public record ControlObjectCursorDto(string AfterName, string AfterId);

// What one node's guard switch needs. `Mode` is what is written on the node itself ("inherit" is
// simply its absence), `Armed`/`Source` what it actually resolves to, and `SourceObjectId` names
// the zone that settled it — the browser paints an inherited guard differently.
public record NodeArmingDto(
  string ObjectId,
  bool Armable,
  bool Configured,
  string Mode,
  bool Armed,
  string Source,
  string SourceObjectId,
  IReadOnlyList<string> Modes);

public record SetArmingDto(string? Mode);

public record AcknowledgeZoneDto(string? Comment);

public record AlarmRejectionDto(string AlarmId, string Reason);

public record AcknowledgeZoneResultDto(
  int Acknowledged,
  IReadOnlyList<AlarmRejectionDto> Rejected);
