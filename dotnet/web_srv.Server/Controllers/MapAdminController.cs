using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Grpc.Core;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using web_srv.Authentication;
using CfgProto = Vms.Cfg;
using MapProto = Vms.Map;

namespace web_srv;

// The write surface of the map: everything an administrator draws or edits — nodes, properties,
// how they look, their geometry, replicas, the state dictionary, zoom levels, mnemoschemes and
// their types, and the files a picture lives in. Editing belongs to Admin → Maps alone; the
// operator never writes. The bodies are the proto messages themselves, and errors come back in
// the same shape as gRPC's — RpcExceptionFilter turns them into { "error": ... }.
[ApiController]
[Authorize(Policy = AuthPolicies.AdminOnly)]
[Route("api/map/admin")]
public class MapAdminController(
  MapProto.MapService.MapServiceClient map,
  CfgProto.CfgService.CfgServiceClient cfg) : ControllerBase
{
  /// Mirrors VmsMap's limit, so an oversized upload is refused here before it is streamed anywhere.
  private const int MaxFileBytes = 20 * 1024 * 1024;
  private const int ChunkBytes = 256 * 1024;

  // A picture of tens of megabytes through two sidecars is not a 10-second call.
  private static readonly TimeSpan UploadTimeout = TimeSpan.FromSeconds(60);

  // A world install writes dozens of rows through two services; an install on a small machine is
  // still seconds, not the ten the ordinary map calls allow.
  private static readonly TimeSpan DemoTimeout = TimeSpan.FromSeconds(120);

  // The wall is a handful of rows once VmsCfg answers. When it does not — it may not be running at
  // all — the world is still the world, so this deadline is a report, not a failed install.
  private static readonly TimeSpan WallTimeout = TimeSpan.FromSeconds(20);

  // The one world today; the button does not choose.
  private const string DemoKey = "umd-datacenter";

  /// What an install or a removal did on both sides of it: VmsMap writes the world, VmsCfg its
  /// video wall. The browser shows the skipped cameras, which is a list of what to provision next,
  /// and the wall error, when VmsCfg could not be reached: the wall is not part of the world.
  public sealed record DemoInstallResult(
    bool Installed,
    int Version,
    int Created,
    int Updated,
    int Removed,
    IReadOnlyList<string> SkippedCameras,
    IReadOnlyList<string> MonitorCameras,
    string? WallError);

  private static CallOptions DemoOptions(CancellationToken ct) =>
    new(deadline: DateTime.UtcNow.Add(DemoTimeout), cancellationToken: ct);

  private static CallOptions WallOptions(CancellationToken ct) =>
    new(deadline: DateTime.UtcNow.Add(WallTimeout), cancellationToken: ct);

  /// VmsCfg's half of the demo, whose failure is reported rather than thrown: the wall is built
  /// from cameras and stands or falls on its own, while the map's world is already written.
  private static async Task<(T? Value, string? Error)> TryWall<T>(
    Func<Task<T>> call, CancellationToken ct) where T : class
  {
    try
    {
      return (await call(), null);
    }
    catch (OperationCanceledException) when (ct.IsCancellationRequested)
    {
      throw;
    }
    catch (Exception e)
    {
      return (null, e.Message);
    }
  }

  public sealed class UploadForm
  {
    public IFormFile? File { get; init; }
  }

  // ── Nodes ───────────────────────────────────────────────────────────────────────────────────

  [HttpPut("objects")]
  public async Task<IActionResult> UpdateObjects([FromBody] JsonElement body, CancellationToken ct)
  {
    var request = MapApi.Parse<MapProto.ObjectList>(body);
    return MapApi.Json(await map.UpdateObjectsAsync(request, MapApi.Options(ct)));
  }

  [HttpDelete("objects")]
  public async Task<IActionResult> DeleteObjects([FromBody] JsonElement body, CancellationToken ct)
  {
    var request = MapApi.Parse<MapProto.ObjectIds>(body);
    return MapApi.Json(await map.DeleteObjectsAsync(request, MapApi.Options(ct)));
  }

  [HttpPut("properties")]
  public async Task<IActionResult> UpdateProperties([FromBody] JsonElement body, CancellationToken ct)
  {
    var request = MapApi.Parse<MapProto.ObjectPropertiesList>(body);
    return MapApi.Json(await map.UpdatePropertiesAsync(request, MapApi.Options(ct)));
  }

  [HttpPut("visual")]
  public async Task<IActionResult> UpdateVisual([FromBody] JsonElement body, CancellationToken ct)
  {
    var request = MapApi.Parse<MapProto.VisualList>(body);
    return MapApi.Json(await map.UpdateVisualAsync(request, MapApi.Options(ct)));
  }

  [HttpPut("figures")]
  public async Task<IActionResult> UpdateFigures([FromBody] JsonElement body, CancellationToken ct)
  {
    var request = MapApi.Parse<MapProto.Figures>(body);
    return MapApi.Json(await map.UpdateFiguresAsync(request, MapApi.Options(ct)));
  }

  /// A picture of a node on another canvas; it shows the owner's states and has no states of its
  /// own. The parent must grant the caller the edit right, which an administrator always has.
  [HttpPost("replicas")]
  public async Task<IActionResult> CreateReplica([FromBody] JsonElement body, CancellationToken ct)
  {
    var request = MapApi.Parse<MapProto.CreateReplicaRequest>(body);
    request.Caller = User.ToMapCaller();

    return MapApi.Json(await map.CreateReplicaAsync(request, MapApi.Options(ct)));
  }

  // ── Dictionaries ────────────────────────────────────────────────────────────────────────────

  [HttpPut("state-descriptions")]
  public async Task<IActionResult> UpdateStateDescriptions(
    [FromBody] JsonElement body, CancellationToken ct)
  {
    var request = MapApi.Parse<MapProto.StateDescriptionList>(body);
    return MapApi.Json(await map.UpdateStateDescriptionsAsync(request, MapApi.Options(ct)));
  }

  [HttpPut("levels")]
  public async Task<IActionResult> UpdateLevels([FromBody] JsonElement body, CancellationToken ct)
  {
    var request = MapApi.Parse<MapProto.LevelList>(body);
    return MapApi.Json(await map.UpdateLevelsAsync(request, MapApi.Options(ct)));
  }

  [HttpDelete("levels")]
  public async Task<IActionResult> DeleteLevels([FromBody] JsonElement body, CancellationToken ct)
  {
    var request = MapApi.Parse<MapProto.ObjectIds>(body);
    return MapApi.Json(await map.DeleteLevelsAsync(request, MapApi.Options(ct)));
  }

  // ── Mnemoschemes ────────────────────────────────────────────────────────────────────────────

  [HttpPut("diagrams")]
  public async Task<IActionResult> UpdateDiagrams([FromBody] JsonElement body, CancellationToken ct)
  {
    var request = MapApi.Parse<MapProto.DiagramList>(body);
    return MapApi.Json(await map.UpdateDiagramsAsync(request, MapApi.Options(ct)));
  }

  [HttpDelete("diagrams")]
  public async Task<IActionResult> DeleteDiagrams([FromBody] JsonElement body, CancellationToken ct)
  {
    var request = MapApi.Parse<MapProto.ObjectIds>(body);
    return MapApi.Json(await map.DeleteDiagramsAsync(request, MapApi.Options(ct)));
  }

  [HttpPut("diagram-types")]
  public async Task<IActionResult> UpdateDiagramTypes(
    [FromBody] JsonElement body, CancellationToken ct)
  {
    var request = MapApi.Parse<MapProto.DiagramTypeList>(body);
    return MapApi.Json(await map.UpdateDiagramTypesAsync(request, MapApi.Options(ct)));
  }

  [HttpDelete("diagram-types")]
  public async Task<IActionResult> DeleteDiagramTypes(
    [FromBody] JsonElement body, CancellationToken ct)
  {
    var request = MapApi.Parse<MapProto.ObjectIds>(body);
    return MapApi.Json(await map.DeleteDiagramTypesAsync(request, MapApi.Options(ct)));
  }

  // ── Demo world ──────────────────────────────────────────────────────────────────────────────

  [HttpGet("demo")]
  public async Task<IActionResult> GetDemoStatus(CancellationToken ct)
  {
    var status = await map.GetDemoStatusAsync(
      new MapProto.InstallDemoRequest { DemoKey = DemoKey }, MapApi.Options(ct));

    return Ok(new { installed = status.Installed, version = status.Version, entities = status.Entities });
  }

  /// The map world first, its wall second: the wall only knows about cameras, and a failure of
  /// the world should not leave a wall behind pointing at absent racks.
  [HttpPost("demo/install")]
  public async Task<IActionResult> InstallDemo(CancellationToken ct)
  {
    var request = new MapProto.InstallDemoRequest { DemoKey = DemoKey };
    var world = await map.InstallDemoAsync(request, DemoOptions(ct));
    var (wall, wallError) = await TryWall(
      async () => await cfg.InstallDemoAsync(
        new CfgProto.InstallDemoRequest { DemoKey = DemoKey }, WallOptions(ct)), ct);

    var skippedCameras = new List<string>(world.SkippedCameras);
    var monitorCameras = new List<string>();
    if (wall is not null)
    {
      skippedCameras.AddRange(wall.SkippedCameras);
      monitorCameras.AddRange(wall.PlacedCameras);
    }

    return Ok(new DemoInstallResult(
      Installed: true,
      Version: world.Version,
      Created: world.Created,
      Updated: world.Updated,
      Removed: world.Removed,
      SkippedCameras: skippedCameras,
      MonitorCameras: monitorCameras,
      WallError: wallError));
  }

  [HttpPost("demo/remove")]
  public async Task<IActionResult> RemoveDemo(CancellationToken ct)
  {
    var request = new MapProto.InstallDemoRequest { DemoKey = DemoKey };
    var report = await map.RemoveDemoAsync(request, DemoOptions(ct));
    var (_, wallError) = await TryWall(
      async () => await cfg.RemoveDemoAsync(
        new CfgProto.InstallDemoRequest { DemoKey = DemoKey }, WallOptions(ct)), ct);

    return Ok(new DemoInstallResult(
      Installed: false,
      Version: report.Version,
      Created: report.Created,
      Updated: report.Updated,
      Removed: report.Removed,
      SkippedCameras: [],
      MonitorCameras: [],
      WallError: wallError));
  }

  // ── Event routing ───────────────────────────────────────────────────────────────────────────

  /// The subscriptions the router in VmsSingleton executes. VmsCfg owns and validates them; the
  /// panel is only a view over that contract, so a selector the router cannot match is refused at
  /// save time rather than silently ignored at run time.
  [HttpGet("event-subscriptions")]
  public async Task<IActionResult> ListEventSubscriptions(CancellationToken ct)
  {
    var response = await cfg.ListEventSubscriptionsAsync(
      new CfgProto.ListEventSubscriptionsRequest(), MapApi.Options(ct));
    return MapApi.Json(response);
  }

  [HttpPut("event-subscriptions")]
  public async Task<IActionResult> UpsertEventSubscription(
    [FromBody] JsonElement body, CancellationToken ct)
  {
    var request = MapApi.Parse<CfgProto.UpsertEventSubscriptionRequest>(body);
    var status = await cfg.UpsertEventSubscriptionAsync(request, MapApi.Options(ct));

    return status.Success ? Ok(status) : BadRequest(new { error = status.Message });
  }

  [HttpDelete("event-subscriptions/{id}")]
  public async Task<IActionResult> RemoveEventSubscription(string id, CancellationToken ct)
  {
    var status = await cfg.RemoveEventSubscriptionAsync(
      new CfgProto.RemoveEventSubscriptionRequest { Id = id }, MapApi.Options(ct));

    return status.Success ? Ok(status) : BadRequest(new { error = status.Message });
  }

  // ── Files ───────────────────────────────────────────────────────────────────────────────────

  /// A plan, a diagram background or an icon, stored as a row of `files` in ta_map. The row is
  /// addressed by its own id afterwards (`api/map/files/{id}`), which is what a diagram type's or
  /// a visual's `file_id` carries.
  [HttpPost("files")]
  [RequestSizeLimit(MaxFileBytes + 1024 * 1024)]
  [RequestFormLimits(MultipartBodyLengthLimit = MaxFileBytes + 1024 * 1024)]
  public async Task<IActionResult> UploadFile([FromForm] UploadForm form, CancellationToken ct)
  {
    if (form.File is null || form.File.Length == 0)
      return BadRequest(new { error = "No file was sent" });

    if (form.File.Length > MaxFileBytes)
    {
      return StatusCode(StatusCodes.Status413PayloadTooLarge,
        new { error = $"A file may not exceed {MaxFileBytes / (1024 * 1024)} MiB" });
    }

    var options = new CallOptions(
      deadline: DateTime.UtcNow.Add(UploadTimeout), cancellationToken: ct);

    using var call = map.PutFile(options);
    await call.RequestStream.WriteAsync(
      new MapProto.FileUploadChunk { Mime = form.File.ContentType }, ct);

    await using (var input = form.File.OpenReadStream())
    {
      var buffer = new byte[ChunkBytes];
      int read;
      while ((read = await input.ReadAsync(buffer, ct)) > 0)
      {
        await call.RequestStream.WriteAsync(
          new MapProto.FileUploadChunk { Data = ByteString.CopyFrom(buffer, 0, read) }, ct);
      }
    }

    await call.RequestStream.CompleteAsync();
    return MapApi.Json(await call.ResponseAsync);
  }
}
