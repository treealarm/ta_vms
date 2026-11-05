using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using VmsMap.Clients;
using VmsMap.Db;
using VmsMap.Domain;

namespace VmsMap.Adapter
{
  // Keeps the camera's place in the tree in step with VmsCfg: one canonical node per camera, under
  // the space of its reference placement when it has one, under its server node otherwise. The
  // camera id cannot be recovered from the node id (it is an MD5), so camera_node remembers the
  // pair for every canonical node, placed or not.
  internal sealed class CameraNodeService : ICameraNodeService
  {
    private const string ServerIdVersion = "server";

    private readonly VmsMapDbContext _db;
    private readonly IMapService _mapService;
    private readonly IMapUpdateService _mapUpdateService;
    private readonly ILogger<CameraNodeService> _logger;

    public CameraNodeService(
      VmsMapDbContext db,
      IMapService mapService,
      IMapUpdateService mapUpdateService,
      ILogger<CameraNodeService> logger)
    {
      _db = db ?? throw new ArgumentNullException(nameof(db));
      _mapService = mapService ?? throw new ArgumentNullException(nameof(mapService));
      _mapUpdateService = mapUpdateService ?? throw new ArgumentNullException(nameof(mapUpdateService));
      _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Creates or refreshes the camera's node and its parent. A placed camera is left where its
    /// reference placement put it; an unplaced one hangs under its server's node.
    /// </summary>
    public async Task EnsureCameraAsync(CameraInfo camera, CancellationToken ct)
    {
      if (string.IsNullOrWhiteSpace(camera.CameraId))
        return;

      var canonicalId = CameraObjectId.For(camera.CameraId);
      await using var tx = _db.Database.IsRelational()
        ? await _db.Database.BeginTransactionAsync(ct)
        : null;

      await EnsureCameraNodeRowAsync(canonicalId, camera.CameraId, ct);

      var reference = await _db.CameraPlacements.AsNoTracking()
        .FirstOrDefaultAsync(p => p.camera_id == camera.CameraId && p.is_reference, ct);

      string? parentId;
      if (reference is not null)
      {
        // SpatialDb owns the parent of a placed camera; the adapter only leaves it alone.
        var guid = reference.object_id;
        parentId = (await _db.Markers.AsNoTracking()
          .Where(m => m.id == guid)
          .Select(m => m.parent_id)
          .FirstOrDefaultAsync(ct))?.ToString();
      }
      else
      {
        parentId = string.IsNullOrWhiteSpace(camera.ServerId)
          ? null
          : await EnsureServerAsync(camera.ServerId, ct);
      }

      // The periodic reconcile runs this for every camera; a write that changes nothing would bump
      // the row's xmin and make the watch stream report a change that did not happen.
      var canonicalGuid = Utils.ConvertObjectIdToGuid(canonicalId);
      var parentGuid = Utils.ConvertObjectIdToGuid(parentId);
      var marker = canonicalGuid is null
        ? null
        : await _db.Markers.AsNoTracking()
            .FirstOrDefaultAsync(m => m.id == canonicalGuid.Value, ct);

      if (marker is null || marker.name != camera.Name || marker.parent_id != parentGuid)
      {
        await _mapService.UpdateHierarchyAsync([
          new BaseMarkerDTO
          {
            id = canonicalId,
            parent_id = parentId,
            name = camera.Name,
          }
        ]);
      }
      else
      {
        // Save a camera_node row that was missing without touching the node itself.
        await _db.SaveChangesAsync(ct);
      }

      if (tx is not null)
        await tx.CommitAsync(ct);
    }

    /// <summary>
    /// Repairs the parent after a placement was removed or a node was moved: a reference
    /// placement still decides, otherwise the camera returns under its server.
    /// </summary>
    public Task ReconcileParentAsync(CameraInfo camera, CancellationToken ct) =>
      EnsureCameraAsync(camera, ct);

    /// <summary>
    /// Removes the camera from the tree: its node, every picture of it, its placement rows and its
    /// observations. There is no foreign key from those tables to VmsCfg's camera table, so this
    /// is the replacement for the cascade that used to do it.
    /// </summary>
    public async Task RemoveCameraAsync(string cameraId, CancellationToken ct)
    {
      if (string.IsNullOrWhiteSpace(cameraId))
        return;

      var canonicalId = CameraObjectId.For(cameraId);
      var canonicalGuid = Utils.ConvertObjectIdToGuid(canonicalId);
      if (canonicalGuid is null)
        return;

      var replicaIds = await _db.Markers
        .Where(m => m.owner_id == canonicalGuid.Value)
        .Select(m => m.id)
        .ToListAsync(ct);

      var nodes = replicaIds
        .Select(id => Utils.ConvertGuidToObjectId(id))
        .Append(canonicalId)
        .ToList();

      await using var tx = _db.Database.IsRelational()
        ? await _db.Database.BeginTransactionAsync(ct)
        : null;

      _db.CameraPlacements.RemoveRange(
        await _db.CameraPlacements.Where(p => p.camera_id == cameraId).ToListAsync(ct));
      _db.PtzGeoObservations.RemoveRange(
        await _db.PtzGeoObservations.Where(o => o.camera_id == cameraId).ToListAsync(ct));
      _db.GroundObservations.RemoveRange(
        await _db.GroundObservations.Where(o => o.camera_id == cameraId).ToListAsync(ct));
      _db.CameraNodes.RemoveRange(
        await _db.CameraNodes.Where(n => n.object_id == canonicalGuid.Value).ToListAsync(ct));

      await _db.SaveChangesAsync(ct);

      // The node delete also removes its subtree and lets MapUpdateService recompute the alarms of
      // the parents; the replica pictures are leaves of their own spaces.
      if (await _db.Markers.AnyAsync(m => m.id == canonicalGuid.Value) || replicaIds.Count > 0)
      {
        await _mapUpdateService.Delete(nodes);
      }

      if (tx is not null)
        await tx.CommitAsync(ct);

      _logger.LogInformation("Removed camera '{CameraId}' from the map tree", cameraId);
    }

    /// <summary>
    /// Removes every camera node VmsCfg no longer lists. A camera deleted while this replica was
    /// down has no event left to deliver; the startup resync is what catches it.
    /// </summary>
    public async Task RemoveUnknownAsync(IReadOnlySet<string> known, CancellationToken ct)
    {
      var stored = await _db.CameraNodes
        .Select(n => n.camera_id)
        .Distinct()
        .ToListAsync(ct);

      foreach (var cameraId in stored.Where(id => !known.Contains(id)))
        await RemoveCameraAsync(cameraId, ct);
    }

    /// <summary>Merges one property into the camera's node without dropping the others.</summary>
    public Task SetPropertyAsync(
      string cameraId, string name, string value, CancellationToken ct) =>
      _mapService.UpdatePropNotDeleteAsync([
        new ObjPropsDTO
        {
          id = CameraObjectId.For(cameraId),
          extra_props = [new ObjExtraPropertyDTO { prop_name = name, str_val = value }],
        }
      ]);

    private async Task<string> EnsureServerAsync(string serverId, CancellationToken ct)
    {
      var id = ObjectIdGenerator.GenerateObjectId(serverId, ServerIdVersion);
      await _mapService.UpdateHierarchyAsync([
        new BaseMarkerDTO { id = id, name = serverId }
      ]);
      return id;
    }

    private async Task EnsureCameraNodeRowAsync(
      string canonicalId, string cameraId, CancellationToken ct)
    {
      var guid = Utils.ConvertObjectIdToGuid(canonicalId);
      if (guid is null)
        return;

      var row = await _db.CameraNodes.FirstOrDefaultAsync(n => n.object_id == guid.Value, ct);
      if (row is null)
        _db.CameraNodes.Add(new DBCameraNode { object_id = guid.Value, camera_id = cameraId });
      else if (row.camera_id != cameraId)
        row.camera_id = cameraId;
    }
  }
}
