using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using web_srv.Authentication;
using web_srv.Authorization;
using MapProto = Vms.Map;

namespace web_srv;

// The read surface of the map: objects, states, events, tracks, values, diagrams and levels. Every
// action is exempt from the camera-id filter because VmsMap applies the caller's rights itself, on
// the same tree the nodes live on.
[ApiController]
[Authorize(Policy = AuthPolicies.Authenticated)]
[Route("api/map/objects")]
[NoRights("Map reads are filtered by VmsMap against the caller's rights")]
public class MapObjectsController(MapProto.MapService.MapServiceClient map) : ControllerBase
{
  [HttpGet("parent")]
  public async Task<IActionResult> GetByParent(
    [FromQuery] string? parentId, [FromQuery] string? startId, [FromQuery] string? endId,
    [FromQuery] int count, CancellationToken ct)
  {
    var response = await map.GetByParentAsync(new MapProto.GetByParentRequest
    {
      ParentId = parentId ?? string.Empty,
      StartId = startId ?? string.Empty,
      EndId = endId ?? string.Empty,
      Count = count,
      Caller = User.ToMapCaller(),
    }, MapApi.Options(ct));

    return MapApi.Json(response);
  }

  [HttpPost("by-ids")]
  public async Task<IActionResult> GetByIds(
    [FromBody] List<string> ids, CancellationToken ct)
  {
    var response = await map.GetByIdsAsync(new MapProto.GetByIdsRequest
    {
      Ids = { ids },
      Caller = User.ToMapCaller(),
    }, MapApi.Options(ct));

    return MapApi.Json(response);
  }

  [HttpPost("markers")]
  public async Task<IActionResult> GetMarkersByIds(
    [FromBody] List<string> ids, CancellationToken ct)
  {
    var response = await map.GetMarkersByIdsAsync(new MapProto.GetByIdsRequest
    {
      Ids = { ids },
      Caller = User.ToMapCaller(),
    }, MapApi.Options(ct));

    return MapApi.Json(response);
  }

  [HttpPost("by-box")]
  public async Task<IActionResult> GetByBox(
    [FromBody] JsonElement body, CancellationToken ct)
  {
    var request = MapApi.Parse<MapProto.GetByBoxRequest>(body);
    request.Caller = User.ToMapCaller();

    return MapApi.Json(await map.GetByBoxAsync(request, MapApi.Options(ct)));
  }

  [HttpGet("by-name")]
  public async Task<IActionResult> GetByName(
    [FromQuery] string name, CancellationToken ct)
  {
    var response = await map.GetByNameAsync(new MapProto.GetByNameRequest
    {
      Name = name ?? string.Empty,
      Caller = User.ToMapCaller(),
    }, MapApi.Options(ct));

    return MapApi.Json(response);
  }

  [HttpPost("by-params")]
  public async Task<IActionResult> GetByParams(
    [FromBody] JsonElement body, CancellationToken ct)
  {
    var request = MapApi.Parse<MapProto.GetByParamsRequest>(body);
    request.Caller = User.ToMapCaller();

    return MapApi.Json(await map.GetByParamsAsync(request, MapApi.Options(ct)));
  }

  [HttpGet("{id}/props")]
  public async Task<IActionResult> GetObjProps(string id, CancellationToken ct)
  {
    var response = await map.GetObjPropsAsync(new MapProto.GetObjPropsRequest
    {
      Id = id,
      Caller = User.ToMapCaller(),
    }, MapApi.Options(ct));

    return MapApi.Json(response);
  }
}

[ApiController]
[Authorize(Policy = AuthPolicies.Authenticated)]
[Route("api/map/states")]
[NoRights("Map reads are filtered by VmsMap against the caller's rights")]
public class MapStatesController(MapProto.MapService.MapServiceClient map) : ControllerBase
{
  [HttpPost]
  public async Task<IActionResult> GetStates(
    [FromBody] List<string> ids, CancellationToken ct)
  {
    var response = await map.GetStatesAsync(new MapProto.GetStatesRequest
    {
      Ids = { ids },
      Caller = User.ToMapCaller(),
    }, MapApi.Options(ct));

    return MapApi.Json(response);
  }

  [HttpPost("visual")]
  public async Task<IActionResult> GetVisualStates(
    [FromBody] List<string> ids, CancellationToken ct)
  {
    var response = await map.GetVisualStatesAsync(new MapProto.GetStatesRequest
    {
      Ids = { ids },
      Caller = User.ToMapCaller(),
    }, MapApi.Options(ct));

    return MapApi.Json(response);
  }

  [HttpPost("alarmed")]
  public async Task<IActionResult> GetAlarmedStates(
    [FromBody] List<string> ids, CancellationToken ct)
  {
    var response = await map.GetAlarmedStatesAsync(new MapProto.GetStatesRequest
    {
      Ids = { ids },
      Caller = User.ToMapCaller(),
    }, MapApi.Options(ct));

    return MapApi.Json(response);
  }

  [HttpPost("descriptions")]
  public async Task<IActionResult> GetStateDescriptions(
    [FromBody] List<string> states, CancellationToken ct)
  {
    var response = await map.GetStateDescriptionsAsync(new MapProto.GetStateDescriptionsRequest
    {
      States = { states },
    }, MapApi.Options(ct));

    return MapApi.Json(response);
  }
}

[ApiController]
[Authorize(Policy = AuthPolicies.Authenticated)]
[Route("api/map/events")]
[NoRights("Map reads are filtered by VmsMap against the caller's rights")]
public class MapEventsController(MapProto.MapService.MapServiceClient map) : ControllerBase
{
  [HttpPost("filter")]
  public async Task<IActionResult> GetByFilter(
    [FromBody] JsonElement body, CancellationToken ct)
  {
    var request = MapApi.Parse<MapProto.GetEventsByFilterRequest>(body);
    request.Caller = User.ToMapCaller();

    return MapApi.Json(await map.GetEventsByFilterAsync(request, MapApi.Options(ct)));
  }
}

[ApiController]
[Authorize(Policy = AuthPolicies.Authenticated)]
[Route("api/map/tracks")]
[NoRights("Map reads are filtered by VmsMap against the caller's rights")]
public class MapTracksController(MapProto.MapService.MapServiceClient map) : ControllerBase
{
  [HttpPost("filter")]
  public async Task<IActionResult> GetByFilter(
    [FromBody] JsonElement body, CancellationToken ct)
  {
    var request = MapApi.Parse<MapProto.GetTracksByFilterRequest>(body);
    request.Caller = User.ToMapCaller();

    return MapApi.Json(await map.GetTracksByFilterAsync(request, MapApi.Options(ct)));
  }

  [HttpPost("box")]
  public async Task<IActionResult> GetByBox(
    [FromBody] JsonElement body, CancellationToken ct)
  {
    var request = MapApi.Parse<MapProto.GetTracksByBoxRequest>(body);
    request.Caller = User.ToMapCaller();

    return MapApi.Json(await map.GetTracksByBoxAsync(request, MapApi.Options(ct)));
  }
}

[ApiController]
[Authorize(Policy = AuthPolicies.Authenticated)]
[Route("api/map/values")]
[NoRights("Map reads are filtered by VmsMap against the caller's rights")]
public class MapValuesController(MapProto.MapService.MapServiceClient map) : ControllerBase
{
  [HttpPost("by-owners")]
  public async Task<IActionResult> GetByOwners(
    [FromBody] List<string> ids, CancellationToken ct)
  {
    var response = await map.GetValuesByOwnersAsync(new MapProto.GetValuesByOwnersRequest
    {
      Ids = { ids },
      Caller = User.ToMapCaller(),
    }, MapApi.Options(ct));

    return MapApi.Json(response);
  }
}

[ApiController]
[Authorize(Policy = AuthPolicies.Authenticated)]
[Route("api/map/diagrams")]
[NoRights("Map reads are filtered by VmsMap against the caller's rights")]
public class MapDiagramsController(MapProto.MapService.MapServiceClient map) : ControllerBase
{
  [HttpGet("{id}/content")]
  public async Task<IActionResult> GetContent(
    string id, [FromQuery] int depth, CancellationToken ct)
  {
    var response = await map.GetDiagramContentAsync(new MapProto.GetDiagramContentRequest
    {
      DiagramId = id,
      Depth = depth,
      Caller = User.ToMapCaller(),
    }, MapApi.Options(ct));

    return MapApi.Json(response);
  }

  [HttpPost("by-ids")]
  public async Task<IActionResult> GetByIds(
    [FromBody] List<string> ids, CancellationToken ct)
  {
    var response = await map.GetDiagramsByIdsAsync(new MapProto.GetByIdsRequest
    {
      Ids = { ids },
      Caller = User.ToMapCaller(),
    }, MapApi.Options(ct));

    return MapApi.Json(response);
  }

  [HttpPost("types")]
  public async Task<IActionResult> GetTypes(
    [FromBody] JsonElement body, CancellationToken ct)
  {
    var request = MapApi.Parse<MapProto.GetDiagramTypesRequest>(body);
    return MapApi.Json(await map.GetDiagramTypesAsync(request, MapApi.Options(ct)));
  }
}

[ApiController]
[Authorize(Policy = AuthPolicies.Authenticated)]
[Route("api/map/levels")]
[NoRights("Scale layers are not a camera resource")]
public class MapLevelsController(MapProto.MapService.MapServiceClient map) : ControllerBase
{
  [HttpGet]
  public async Task<IActionResult> GetAll(CancellationToken ct)
  {
    var response = await map.GetLevelsAsync(
      new Google.Protobuf.WellKnownTypes.Empty(), MapApi.Options(ct));
    return MapApi.Json(response);
  }

  [HttpGet("{zoomLevel}")]
  public async Task<IActionResult> GetOne(string zoomLevel, CancellationToken ct)
  {
    var response = await map.GetLevelAsync(new MapProto.GetLevelRequest
    {
      ZoomLevel = zoomLevel,
    }, MapApi.Options(ct));

    return MapApi.Json(response);
  }
}
