using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using web_srv.Authentication;
using web_srv.Authorization;
using MapProto = Vms.Map;

namespace web_srv;

// The operator's guarding panel. The list is one page of a space's sensors, already filtered by
// the caller's `view_live` and with the effective arming resolved — VmsMap owns both, so nothing
// here decides what a user may see. The toggle is not here: it is the existing
// PUT api/DeviceIo/cameras/{id}/digital-inputs/{token}/arm-mode, the one command that writes.
[ApiController]
[Authorize(Policy = AuthPolicies.Authenticated)]
[Route("api/guarding")]
public class GuardingController(MapProto.MapService.MapServiceClient map) : ControllerBase
{
  [HttpGet("sensors")]
  // Source = Action: the list filters itself in VmsMap, like the map reads. The filter only
  // records that a decision was made.
  [RequiresRight(RightActions.ViewLive, Source = RightSource.Action)]
  public async Task<IActionResult> ListSensors(
    [FromQuery] string? spaceId,
    [FromQuery] int pageSize,
    [FromQuery] string? afterName,
    [FromQuery] string? afterId,
    CancellationToken ct)
  {
    var request = new MapProto.ListGuardSensorsRequest
    {
      SpaceId = spaceId ?? string.Empty,
      PageSize = pageSize,
      Caller = User.ToMapCaller(),
    };

    // Half a cursor is a caller's mistake, not a position: VmsMap refuses it as InvalidArgument,
    // and half of it would silently restart from the beginning.
    if (!string.IsNullOrWhiteSpace(afterName) || !string.IsNullOrWhiteSpace(afterId))
    {
      request.After = new MapProto.GuardSensorCursor
      {
        AfterName = afterName ?? string.Empty,
        AfterId = afterId ?? string.Empty,
      };
    }

    var response = await map.ListGuardSensorsAsync(request, MapApi.Options(ct));

    return Ok(new GuardSensorPageDto(
      response.Sensors.Select(s => new GuardSensorDto(
        s.SourceKind,
        s.SourceId,
        s.PartKind,
        s.PartToken,
        s.ObjectId,
        s.Name,
        s.CameraName,
        s.SpaceId,
        s.SpaceName,
        s.ArmMode,
        s.AlarmEnabled,
        s.Configured,
        s.Armed,
        s.ArmingSource,
        s.CanControl)).ToList(),
      response.HasMore,
      response.HasMore && response.Next is not null
        ? new GuardSensorCursorDto(response.Next.AfterName, response.Next.AfterId)
        : null));
  }
}

public record GuardSensorDto(
  string SourceKind,
  string SourceId,
  string PartKind,
  string PartToken,
  string ObjectId,
  string Name,
  string CameraName,
  string SpaceId,
  string SpaceName,
  string ArmMode,
  bool AlarmEnabled,
  bool Configured,
  bool Armed,
  string ArmingSource,
  bool CanControl);

public record GuardSensorPageDto(
  IReadOnlyList<GuardSensorDto> Sensors,
  bool HasMore,
  GuardSensorCursorDto? Next);

public record GuardSensorCursorDto(string AfterName, string AfterId);
