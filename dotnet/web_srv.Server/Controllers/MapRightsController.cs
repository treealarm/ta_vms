using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using web_srv.Authentication;
using MapProto = Vms.Map;

namespace web_srv;

// The rights editor: rows on tree nodes, the effective set for one user, the action dictionary and
// the open/enforced switch. Administration only — the operator never changes rights.
[ApiController]
[Route("api/map/rights")]
[Authorize(Policy = AuthPolicies.AdminOnly)]
public class MapRightsController(MapProto.MapService.MapServiceClient map) : ControllerBase
{
  [HttpGet]
  public async Task<IActionResult> GetRights(
    [FromQuery] string? objectId, CancellationToken ct)
  {
    var ids = (objectId ?? string.Empty)
      .Split(',', System.StringSplitOptions.RemoveEmptyEntries | System.StringSplitOptions.TrimEntries)
      .ToList();

    var response = await map.GetRightsAsync(new MapProto.ObjectIds { Ids = { ids } },
      MapApi.Options(ct));
    return MapApi.Json(response);
  }

  [HttpPost("effective")]
  public async Task<IActionResult> GetEffectiveRights(
    [FromBody] JsonElement body, CancellationToken ct)
  {
    var request = MapApi.Parse<MapProto.EffectiveRightsRequest>(body);
    // The administrator asks about somebody else; the caller is the admin, and the answer is the
    // subject's effective set.
    request.Caller ??= new MapProto.Caller();
    request.Caller.Unrestricted = false;

    return MapApi.Json(await map.GetEffectiveRightsAsync(request, MapApi.Options(ct)));
  }

  [HttpGet("actions")]
  public async Task<IActionResult> GetActions(CancellationToken ct)
  {
    var response = await map.GetRightActionsAsync(
      new Google.Protobuf.WellKnownTypes.Empty(), MapApi.Options(ct));
    return MapApi.Json(response);
  }

  [HttpGet("mode")]
  public async Task<IActionResult> GetMode(CancellationToken ct)
  {
    var response = await map.GetRightsModeAsync(
      new Google.Protobuf.WellKnownTypes.Empty(), MapApi.Options(ct));
    return MapApi.Json(response);
  }

  [HttpPut("mode")]
  public async Task<IActionResult> SetMode(
    [FromBody] JsonElement body, CancellationToken ct)
  {
    var request = MapApi.Parse<MapProto.SetRightsModeRequest>(body);
    return MapApi.Json(await map.SetRightsModeAsync(request, MapApi.Options(ct)));
  }

  [HttpPut]
  public async Task<IActionResult> UpdateRights(
    [FromBody] JsonElement body, CancellationToken ct)
  {
    var request = MapApi.Parse<MapProto.RightList>(body);
    return MapApi.Json(await map.UpdateRightsAsync(request, MapApi.Options(ct)));
  }

  [HttpDelete]
  public async Task<IActionResult> DeleteRights(
    [FromBody] JsonElement body, CancellationToken ct)
  {
    var request = MapApi.Parse<MapProto.RightKeyList>(body);
    return MapApi.Json(await map.DeleteRightsAsync(request, MapApi.Options(ct)));
  }
}
