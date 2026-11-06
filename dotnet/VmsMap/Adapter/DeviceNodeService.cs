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
  // Keeps the camera's place in the tree in step with VmsCfg: one canonical node per camera,
  // always under its server's node. Where the camera is drawn is a replica's business (SpatialDb),
  // never the canonical node's: the canonical tree of a device is one place, and a camera placed on
  // a map, a plan or a diagram does not move its meaning there. The camera id cannot be recovered
  // from the node id (it is an MD5), so device_node remembers the key for every canonical node.
  // The same registry will hold the camera's parts when the adapter creates them.
  internal sealed class DeviceNodeService : IDeviceNodeService
  {
    private readonly VmsMapDbContext _db;
    private readonly IMapService _mapService;
    private readonly IMapUpdateService _mapUpdateService;
    private readonly ICameraCatalog _catalog;
    private readonly IDeviceSettingsSource _settings;
    private readonly ILogger<DeviceNodeService> _logger;

    public DeviceNodeService(
      VmsMapDbContext db,
      IMapService mapService,
      IMapUpdateService mapUpdateService,
      ICameraCatalog catalog,
      IDeviceSettingsSource settings,
      ILogger<DeviceNodeService> logger)
    {
      _db = db ?? throw new ArgumentNullException(nameof(db));
      _mapService = mapService ?? throw new ArgumentNullException(nameof(mapService));
      _mapUpdateService = mapUpdateService ?? throw new ArgumentNullException(nameof(mapUpdateService));
      _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
      _settings = settings ?? throw new ArgumentNullException(nameof(settings));
      _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Creates or refreshes the camera's canonical node, under its server's node. The parent is
    /// always the server: a placement is a replica's business, so a placed camera and an unplaced
    /// one stand in the same place of the canonical tree.
    /// </summary>
    public async Task EnsureCameraAsync(CameraInfo camera, CancellationToken ct)
    {
      if (string.IsNullOrWhiteSpace(camera.CameraId))
        return;

      var canonicalId = CameraObjectId.For(camera.CameraId);
      await using var tx = _db.Database.IsRelational()
        ? await _db.Database.BeginTransactionAsync(ct)
        : null;

      // The node row is written before device_node, not merely in the same SaveChanges: the
      // device_node row references objects.id, and EF does not know that relationship — it emits
      // the inserts in tracking order, which put device_node first and the foreign key refused a
      // brand-new camera's node.
      var parentId = string.IsNullOrWhiteSpace(camera.ServerId)
        ? null
        : await EnsureServerAsync(camera.ServerId, ct);

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

      // Now that the object exists, the index row can point at it. This also saves a row that was
      // missing without touching a node that was already right.
      await EnsureDeviceNodeRowAsync(canonicalId, camera.CameraId, ct);
      await _db.SaveChangesAsync(ct);

      if (tx is not null)
        await tx.CommitAsync(ct);
    }

    /// <summary>
    /// Puts the canonical node back under its server. The same call as ensuring it: the parent no
    /// longer depends on where, or whether, the camera is placed.
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
      // Every row the camera owns, its parts included: the map update below removes their nodes
      // through the subtree, and the foreign key would cascade anyway, but a replica removed here
      // must not leave the registry pointing at a node that is about to go.
      _db.DeviceNodes.RemoveRange(
        await _db.DeviceNodes
          .Where(n => n.source_kind == DeviceObjectId.CameraSource && n.source_id == cameraId)
          .ToListAsync(ct));

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
      var stored = await _db.DeviceNodes
        .Where(n => n.source_kind == DeviceObjectId.CameraSource
                    && n.part_kind == DeviceObjectId.DevicePart)
        .Select(n => n.source_id)
        .Distinct()
        .ToListAsync(ct);

      foreach (var cameraId in stored.Where(id => !known.Contains(id)))
        await RemoveCameraAsync(cameraId, ct);
    }

    /// <summary>
    /// Creates or refreshes a part's canonical node under the camera's node, and answers its id.
    /// Idempotent: a snapshot or an event that names a part already in the tree changes nothing.
    /// No geometry is created — a part is placed on a map by its own replica, which does not exist
    /// in M2.4.
    /// </summary>
    public async Task<string?> EnsureAsync(
      string sourceKind, string sourceId, string partKind, string partToken, CancellationToken ct)
    {
      if (!string.Equals(sourceKind, DeviceObjectId.CameraSource, StringComparison.Ordinal))
        return null;

      var ensured = await EnsurePartsAsync(
        sourceId, [(partKind, partToken)], ct);

      return ensured.GetValueOrDefault((partKind, partToken));
    }

    /// <summary>
    /// Ensures every part of one camera in one pass. A snapshot names all of them at once, and
    /// asking VmsCfg and the settings cache once per camera instead of once per part is what keeps
    /// a hundred thousand cameras a sweep rather than a storm.
    /// </summary>
    public async Task<Dictionary<(string Kind, string Token), string>> EnsurePartsAsync(
      string sourceId,
      IReadOnlyList<(string Kind, string Token)> parts,
      CancellationToken ct)
    {
      var result = new Dictionary<(string, string), string>();
      if (string.IsNullOrWhiteSpace(sourceId) || parts.Count == 0)
        return result;

      var camera = await _catalog.GetAsync(sourceId, ct);
      if (camera is null)
        return result;

      await EnsureCameraAsync(camera, ct);

      var configured = await _settings.ListAsync(sourceId, ct);
      var byKey = configured.ToDictionary(
        s => (s.Kind, s.Token), s => s, EqualityComparer<(string, string)>.Default);

      foreach (var part in parts.Distinct())
      {
        if (string.IsNullOrWhiteSpace(part.Kind) || string.IsNullOrWhiteSpace(part.Token))
          continue;

        var name = byKey.TryGetValue(part, out var settings) ? settings.Name : null;
        var id = await EnsurePartNodeAsync(camera, sourceId, part.Kind, part.Token, name, ct);
        if (id is not null)
          result[part] = id;
      }

      return result;
    }

    private async Task<string?> EnsurePartNodeAsync(
      CameraInfo camera,
      string sourceId,
      string partKind,
      string partToken,
      string? configuredName,
      CancellationToken ct)
    {
      var name = !string.IsNullOrWhiteSpace(configuredName)
        ? configuredName!
        : $"{camera.Name} / {partToken}";

      var partId = DeviceObjectId.For(DeviceObjectId.CameraSource, sourceId, partKind, partToken);
      var partGuid = Utils.ConvertObjectIdToGuid(partId);
      var parentGuid = Utils.ConvertObjectIdToGuid(CameraObjectId.For(sourceId));
      if (partGuid is null)
        return null;

      var marker = await _db.Markers.AsNoTracking()
        .FirstOrDefaultAsync(m => m.id == partGuid.Value, ct);

      if (marker is null || marker.name != name || marker.parent_id != parentGuid)
      {
        await _mapService.UpdateHierarchyAsync([
          new BaseMarkerDTO
          {
            id = partId,
            parent_id = CameraObjectId.For(sourceId),
            name = name,
          }
        ]);
      }

      await EnsurePartRowAsync(
        DeviceObjectId.CameraSource, sourceId, partKind, partToken, partGuid.Value, ct);
      return partId;
    }

    /// <summary>
    /// Re-reads the names of a camera's parts after device settings changed. The set is small (the
    /// parts of one camera), and the camera id is the leftmost part of the registry's unique key.
    /// </summary>
    public async Task RefreshPartNamesAsync(string cameraId, CancellationToken ct)
    {
      if (string.IsNullOrWhiteSpace(cameraId))
        return;

      var parts = await _db.DeviceNodes.AsNoTracking()
        .Where(n => n.source_kind == DeviceObjectId.CameraSource && n.source_id == cameraId
                    && n.part_kind != DeviceObjectId.DevicePart)
        .Select(n => new { n.part_kind, n.part_token })
        .ToListAsync(ct);

      await EnsurePartsAsync(
        cameraId, parts.Select(p => (p.part_kind, p.part_token)).ToList(), ct);
    }

    private async Task EnsurePartRowAsync(
      string sourceKind, string sourceId, string partKind, string partToken, Guid objectId,
      CancellationToken ct)
    {
      if (_db.Database.IsRelational())
      {
        await _db.Database.ExecuteSqlInterpolatedAsync($"""
          INSERT INTO device_node (object_id, source_kind, source_id, part_kind, part_token)
          VALUES ({objectId}, {sourceKind}, {sourceId}::uuid, {partKind}, {partToken})
          ON CONFLICT (source_kind, source_id, part_kind, part_token) DO NOTHING;
          """, ct);
        return;
      }

      var exists = await _db.DeviceNodes.AnyAsync(n =>
        n.source_kind == sourceKind && n.source_id == sourceId
        && n.part_kind == partKind && n.part_token == partToken, ct);
      if (!exists)
      {
        _db.DeviceNodes.Add(new DBDeviceNode
        {
          object_id = objectId,
          source_kind = sourceKind,
          source_id = sourceId,
          part_kind = partKind,
          part_token = partToken,
        });
        await _db.SaveChangesAsync(ct);
      }
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
      var id = ServerNodeId.For(serverId);
      await _mapService.UpdateHierarchyAsync([
        new BaseMarkerDTO { id = id, name = serverId }
      ]);
      return id;
    }

    private async Task EnsureDeviceNodeRowAsync(
      string canonicalId, string cameraId, CancellationToken ct)
    {
      var guid = Utils.ConvertObjectIdToGuid(canonicalId);
      if (guid is null)
        return;

      var row = await _db.DeviceNodes.FirstOrDefaultAsync(n => n.object_id == guid.Value, ct);
      if (row is null)
      {
        _db.DeviceNodes.Add(new DBDeviceNode
        {
          object_id = guid.Value,
          source_kind = DeviceObjectId.CameraSource,
          source_id = cameraId,
          part_kind = DeviceObjectId.DevicePart,
          part_token = string.Empty,
        });
      }
      else
      {
        row.source_kind = DeviceObjectId.CameraSource;
        row.source_id = cameraId;
        row.part_kind = DeviceObjectId.DevicePart;
        row.part_token = string.Empty;
      }
    }
  }
}
