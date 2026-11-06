using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using web_srv.Authentication;
using web_srv.Authorization;
using MapProto = Vms.Map;

namespace web_srv;

// The operator's alarm list and its two transitions. VmsMap decides what this caller may see and
// acknowledge, so the actions are exempt from the gateway's camera filter; the bodies and answers
// are the proto JSON, like the rest of the map surface.
[ApiController]
[Authorize(Policy = AuthPolicies.Authenticated)]
[Route("api/map/alarms")]
[NoRights("VmsMap filters the alarm list by the caller's rights")]
public class AlarmsController(MapProto.MapService.MapServiceClient map) : ControllerBase
{
  // A POST, not a GET: the request is a proto message with two cursors, and keeping its shape
  // identical to the gRPC contract is worth more than being idempotent on the wire.
  [HttpPost]
  public async Task<IActionResult> GetAlarms(
    [FromBody] JsonElement body, CancellationToken ct)
  {
    var request = MapApi.Parse<MapProto.GetAlarmsRequest>(body);
    request.Caller = User.ToMapCaller();

    return MapApi.Json(await map.GetAlarmsAsync(request, MapApi.Options(ct)));
  }

  // 409 when the condition is active and the node is armed; 403 without the `acknowledge` right.
  [HttpPost("{id}/acknowledge")]
  public async Task<IActionResult> Acknowledge(
    string id, [FromBody] AlarmActionDto? body, CancellationToken ct)
  {
    var response = await map.AcknowledgeAlarmAsync(new MapProto.AcknowledgeAlarmRequest
    {
      Id = id,
      Comment = body?.Comment ?? string.Empty,
      Caller = User.ToMapCaller(),
    }, MapApi.Options(ct));

    return MapApi.Json(response);
  }

  // Only an alarm whose condition is already gone may be closed by hand; `reason` says whether it
  // was real.
  [HttpPost("{id}/close")]
  public async Task<IActionResult> Close(
    string id, [FromBody] AlarmCloseDto body, CancellationToken ct)
  {
    var response = await map.CloseAlarmAsync(new MapProto.CloseAlarmRequest
    {
      Id = id,
      Reason = body.Reason ?? string.Empty,
      Comment = body.Comment ?? string.Empty,
      Caller = User.ToMapCaller(),
    }, MapApi.Options(ct));

    return MapApi.Json(response);
  }
}

public sealed class AlarmActionDto
{
  public string? Comment { get; set; }
}

public sealed class AlarmCloseDto
{
  /// <summary>"false_alarm" or "other".</summary>
  public string? Reason { get; set; }
  public string? Comment { get; set; }
}
