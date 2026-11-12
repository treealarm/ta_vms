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
  bool CanControl);

public record ControlObjectPageDto(
  IReadOnlyList<ControlObjectDto> Objects,
  bool HasMore,
  ControlObjectCursorDto? Next);

public record ControlObjectCursorDto(string AfterName, string AfterId);
