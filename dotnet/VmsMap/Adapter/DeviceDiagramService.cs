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
  /// <summary>
  /// Draws a device from its template: one diagram node, and in its regions the replicas of the
  /// parts the operator actually configured.
  ///
  /// The rule that decides what exists lives here and nowhere else. A part that is not configured
  /// has no replica and no state, and the region stays as the template drew it — a template
  /// describes the whole model, not one installation of it. A replica made by hand in the same
  /// diagram is not in <c>device_diagram_parts</c> and is never moved or removed.
  /// </summary>
  /// <remarks>
  /// One transaction and one advisory lock per device: a snapshot and a configuration change
  /// arrive independently, and two runs that both find no diagram would otherwise both create one.
  /// Reconciliation comes before creation — a diagram whose parts have all been unconfigured still
  /// has to lose them, which the early-exit order of the first draft of this plan would not have
  /// allowed.
  /// </remarks>
  internal sealed class DeviceDiagramService : IDeviceDiagramService
  {
    private readonly VmsMapDbContext _db;
    private readonly IMapService _map;
    private readonly IMapUpdateService _updates;
    private readonly IDiagramServiceInternal _diagrams;
    private readonly IDeviceSettingsSource _settings;
    private readonly ICameraCatalog _catalog;
    private readonly ILogger<DeviceDiagramService> _logger;

    public DeviceDiagramService(
      VmsMapDbContext db,
      IMapService map,
      IMapUpdateService updates,
      IDiagramServiceInternal diagrams,
      IDeviceSettingsSource settings,
      ICameraCatalog catalog,
      ILogger<DeviceDiagramService> logger)
    {
      _db = db ?? throw new ArgumentNullException(nameof(db));
      _map = map ?? throw new ArgumentNullException(nameof(map));
      _updates = updates ?? throw new ArgumentNullException(nameof(updates));
      _diagrams = diagrams ?? throw new ArgumentNullException(nameof(diagrams));
      _settings = settings ?? throw new ArgumentNullException(nameof(settings));
      _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
      _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task EnsureAsync(
      string sourceKind, string sourceId, string slot, CancellationToken ct)
    {
      if (!IsSupported(sourceKind, sourceId))
        return;

      // The sweep runs this for every camera on every snapshot, and almost always finds nothing to
      // do. The map's locks are transitive and global: taking them for a read that changes nothing
      // would serialise the whole deployment against itself. So the settled case is decided
      // without a transaction, and only a real change pays for the locks.
      if (await IsSettledAsync(sourceKind, sourceId, slot, ct))
        return;

      await using var tx = await OwnTransactionAsync(ct);

      // The same order every writer of the map's rows takes: the camera-state and alarm locks
      // first, then this device's own. Taking the device lock first and reaching camera-state
      // inside a write is the other half of a deadlock against a camera removal, which holds
      // camera-state while it deletes the very rows this would write.
      await MapLocks.AcquireAllAsync(_db, ct);
      await MapLocks.AcquireDeviceDiagramAsync(_db, sourceKind, sourceId, slot, ct);

      await EnsureCoreAsync(sourceKind, sourceId, slot, ct);

      if (tx is not null)
        await tx.CommitAsync(ct);
    }

    public async Task AttachAsync(
      string sourceKind, string sourceId, string diagramTypeId, string slot, CancellationToken ct)
    {
      if (!IsSupported(sourceKind, sourceId) || string.IsNullOrWhiteSpace(diagramTypeId))
        return;

      var typeId = Utils.ConvertObjectIdToGuid(diagramTypeId);
      if (typeId is null)
        return;

      await using var tx = await OwnTransactionAsync(ct);
      await MapLocks.AcquireAllAsync(_db, ct);
      await MapLocks.AcquireDeviceDiagramAsync(_db, sourceKind, sourceId, slot, ct);

      var row = await _db.DeviceDiagrams.FirstOrDefaultAsync(
        d => d.source_kind == sourceKind && d.source_id == sourceId && d.slot == slot, ct);

      if (row is null)
      {
        row = await CreateDiagramAsync(sourceKind, sourceId, slot, typeId.Value, autoCreated: false, ct);
        if (row is null)
          return;
      }
      else if (row.diagram_type_id != typeId)
      {
        // The administrator's choice replaces whatever the template said before; the replicas are
        // rebuilt below against the new rules.
        await SetTypeAsync(row, typeId.Value, ct);
        row.auto_created = false;
      }

      await ReconcileAsync(row, sourceId, await ConfiguredPartsAsync(sourceId, ct), ct);

      if (tx is not null)
        await tx.CommitAsync(ct);
    }

    /// <summary>
    /// Whether the diagram already says what it should — asked without a transaction, so the
    /// sweep over every camera costs reads and nothing else. A diagram that is not there yet is
    /// never settled: creating it is the one case that is rare enough to pay for the locks.
    /// </summary>
    private async Task<bool> IsSettledAsync(
      string sourceKind, string sourceId, string slot, CancellationToken ct)
    {
      var row = await _db.DeviceDiagrams.AsNoTracking().FirstOrDefaultAsync(
        d => d.source_kind == sourceKind && d.source_id == sourceId && d.slot == slot, ct);

      if (row is null)
        return false;

      // Deleting a type leaves the canvas where it is, and the service stops maintaining it — but
      // a template for the device coming back is the same null: a demo reinstall deletes the type
      // and creates it again under a new id. So a diagram without a type is settled only while
      // nothing fits the device; once something does, the locks are worth taking to adopt it.
      if (row.diagram_type_id is null)
        return (await SelectTypeAsync(sourceId, ct)).TypeId is null;

      var resolved = await ResolveTypeAsync(row, sourceId, ct);
      if (resolved != row.diagram_type_id)
        return false;

      var parts = await ConfiguredPartsAsync(sourceId, ct);

      var rules = await _db.DiagramTypeParts.AsNoTracking()
        .Where(p => p.diagram_type_id == row.diagram_type_id)
        .Select(p => new RegionPartRule(p.region_key, p.part_kind, p.match_by, p.match_value))
        .ToListAsync(ct);

      var binding = DiagramTemplateBinding.Bind(rules, parts.Select(p => p.Ref).ToList());
      var nodesByKey = parts.ToDictionary(p => PartKey(p.Ref), p => p, StringComparer.Ordinal);
      var desired = binding.Bound
        .Where(b => nodesByKey.ContainsKey(PartKey(b.Part)))
        .Select(b => (Part: nodesByKey[PartKey(b.Part)].ObjectId, b.RegionKey))
        .ToHashSet();

      var managed = await _db.DeviceDiagramParts.AsNoTracking()
        .Where(p => p.diagram_object_id == row.object_id)
        .Select(p => new { p.region_key, p.part_object_id, p.replica_object_id })
        .ToListAsync(ct);

      if (managed.Count != desired.Count)
        return false;

      if (!managed.All(m =>
            desired.Contains((Utils.ConvertGuidToObjectId(m.part_object_id), m.region_key))))
        return false;

      // A replica deleted behind the service's back leaves its row claiming a node that is gone.
      var replicas = managed.Select(m => m.replica_object_id).ToList();
      return await _db.Markers.CountAsync(m => replicas.Contains(m.id), ct) == replicas.Count;
    }

    private async Task EnsureCoreAsync(
      string sourceKind, string sourceId, string slot, CancellationToken ct)
    {
      var row = await _db.DeviceDiagrams.FirstOrDefaultAsync(
        d => d.source_kind == sourceKind && d.source_id == sourceId && d.slot == slot, ct);

      if (row is null)
      {
        // Nothing to create from: no template for this model, or not a single part configured for
        // it. A device with a template but no parts would be an empty canvas, and the plan's rule
        // is to skip it — `sim-vehicle` shares `sim-master`'s model and has no parts at all.
        var parts = await ConfiguredPartsAsync(sourceId, ct);
        if (parts.Count == 0)
          return;

        var selection = await SelectTypeAsync(sourceId, ct);
        if (selection.TypeId is null)
        {
          if (selection.Ambiguity is not null)
            _logger.LogWarning("No device diagram for camera {CameraId}: {Ambiguity}",
              sourceId, selection.Ambiguity);
          return;
        }

        row = await CreateDiagramAsync(
          sourceKind, sourceId, slot, Guid.Parse(selection.TypeId), autoCreated: true, ct);
        if (row is null)
          return;

        await ReconcileAsync(row, sourceId, parts, ct);
        return;
      }

      // Reconciliation happens whether or not anything is configured now: a part removed from the
      // settings has to take its replica with it, and an early return would leave it behind.
      var resolved = await ResolveTypeAsync(row, sourceId, ct);
      if (resolved is null)
      {
        // No template any more (the type was deleted and none matches): the canvas and its
        // replicas stay exactly as they are, and nothing maintains them.
        return;
      }

      if (row.diagram_type_id != resolved)
      {
        await SetTypeAsync(row, resolved.Value, ct);
        _logger.LogInformation(
          "Camera {CameraId}: device diagram moved to type {TypeId}", sourceId, resolved);
      }

      await ReconcileAsync(row, sourceId, await ConfiguredPartsAsync(sourceId, ct), ct);
    }

    /// <summary>
    /// The type the diagram should be drawn from now. A type that still describes the device is
    /// kept even if it no longer attaches itself — an administrator may have attached it by hand,
    /// and a device does not have to be redrawn because the model string drifted. When it no longer
    /// fits, a unique auto-attaching match takes over; with none, the old type stays.
    /// </summary>
    private async Task<Guid?> ResolveTypeAsync(
      DBDeviceDiagram row, string cameraId, CancellationToken ct)
    {
      var info = await DeviceInfoAsync(cameraId, ct);
      var selection = info is null ? Never : await SelectTypeAsync(info, ct);

      if (row.diagram_type_id is not { } current)
        return selection.TypeId is null ? null : Guid.Parse(selection.TypeId);

      if (info is null)
        return current;

      var type = await _db.DiagramTypes.AsNoTracking()
        .FirstOrDefaultAsync(t => t.id == current, ct);

      if (type is not null && DiagramModelMatch.Matches(Candidate(type), info.SourceKind, info.Manufacturer, info.Model))
        return current;

      return selection.TypeId is null ? current : Guid.Parse(selection.TypeId);
    }

    private static readonly DiagramModelSelection Never = new(null, null);

    private async Task<DiagramModelSelection> SelectTypeAsync(string cameraId, CancellationToken ct)
    {
      var info = await DeviceInfoAsync(cameraId, ct);
      return info is null ? Never : await SelectTypeAsync(info, ct);
    }

    private async Task<DiagramModelSelection> SelectTypeAsync(DeviceModel info, CancellationToken ct)
    {
      // Types are a dictionary an administrator keeps by hand, not a per-camera table; reading the
      // auto-attaching ones is a single small query.
      var candidates = await _db.DiagramTypes.AsNoTracking()
        .Where(t => t.auto_attach && t.source_kind != null)
        .Select(t => new DiagramTypeCandidate(
          t.id.ToString(), t.source_kind, t.device_manufacturer, t.device_model, t.auto_attach))
        .ToListAsync(ct);

      return DiagramModelMatch.Select(candidates, info.SourceKind, info.Manufacturer, info.Model);
    }

    private static DiagramTypeCandidate Candidate(DBDiagramType type) =>
      new(type.id.ToString(), type.source_kind, type.device_manufacturer, type.device_model,
          type.auto_attach);

    private sealed record DeviceModel(string SourceKind, string Manufacturer, string Model);

    private async Task<DeviceModel?> DeviceInfoAsync(string cameraId, CancellationToken ct)
    {
      var info = await _db.DeviceInfos.AsNoTracking().FirstOrDefaultAsync(
        d => d.source_kind == DeviceObjectId.CameraSource && d.source_id == cameraId, ct);

      return info is null
        ? null
        : new DeviceModel(DeviceObjectId.CameraSource, info.manufacturer, info.model);
    }

    /// <summary>
    /// The parts a rule may bind to: declared by the device (<c>device_node</c>) and configured by
    /// the operator (<c>device_settings</c>). Discovery never creates a settings row, so the row is
    /// exactly the "it is set up in the configuration" the decision asked for.
    /// </summary>
    private async Task<List<ConfiguredPart>> ConfiguredPartsAsync(
      string cameraId, CancellationToken ct)
    {
      var settings = await _settings.ListAsync(cameraId, ct);
      if (settings.Count == 0)
        return [];

      var nodes = await _db.DeviceNodes.AsNoTracking()
        .Where(n => n.source_kind == DeviceObjectId.CameraSource
                    && n.source_id == cameraId
                    && n.part_kind != DeviceObjectId.DevicePart)
        .ToListAsync(ct);

      var nodesByKey = nodes.ToDictionary(
        n => (n.part_kind, n.part_token), n => n.object_id);

      var ids = nodes.Select(n => n.object_id).ToList();
      var names = await _db.Markers.AsNoTracking()
        .Where(m => ids.Contains(m.id))
        .Select(m => new { m.id, m.name })
        .ToDictionaryAsync(m => m.id, m => m.name, ct);

      var parts = new List<ConfiguredPart>();
      foreach (var setting in settings)
      {
        if (!nodesByKey.TryGetValue((setting.Kind, setting.Token), out var objectId))
          continue; // configured, but the device does not declare it: nothing to draw

        var name = names.GetValueOrDefault(objectId) ?? setting.Token;
        parts.Add(new ConfiguredPart(
          Utils.ConvertGuidToObjectId(objectId),
          new DevicePartRef(setting.Kind, setting.Token, setting.Name ?? string.Empty),
          name));
      }

      return parts;
    }

    /// <summary>
    /// Points the node at the type's picture as well as the row at the type: the canvas is what is
    /// drawn, and a row naming a template the canvas does not is blank paper with replicas on it.
    /// The node keeps its own name, as its id keeps its slot — renaming a device's diagram is not
    /// what a template change is about.
    /// </summary>
    private async Task SetTypeAsync(DBDeviceDiagram row, Guid typeId, CancellationToken ct)
    {
      row.diagram_type_id = typeId;
      await _diagrams.UpdateListAsync([
        new DiagramDTO
        {
          id = Utils.ConvertGuidToObjectId(row.object_id),
          dgr_type = Utils.ConvertGuidToObjectId(typeId),
          geometry = null,
          region_id = null,
        }
      ]);
    }

    private sealed record ConfiguredPart(string ObjectId, DevicePartRef Ref, string DisplayName);

    private async Task<DBDeviceDiagram?> CreateDiagramAsync(
      string sourceKind, string sourceId, string slot, Guid typeId, bool autoCreated,
      CancellationToken ct)
    {
      var type = await _db.DiagramTypes.AsNoTracking().FirstOrDefaultAsync(t => t.id == typeId, ct);
      if (type is null)
        return null;

      var camera = await _catalog.GetAsync(sourceId, ct);
      var objectId = DeviceObjectId.ForDeviceDiagram(sourceKind, sourceId, slot);

      await _map.UpdateHierarchyAsync([
        new BaseMarkerDTO
        {
          id = objectId,
          parent_id = CameraObjectId.For(sourceId),
          name = $"{camera?.Name ?? sourceId} / {type.name}",
        }
      ]);

      await _diagrams.UpdateListAsync([
        new DiagramDTO
        {
          id = objectId,
          dgr_type = Utils.ConvertGuidToObjectId(typeId),
          // Not on any map: the diagram hangs under its device and is opened from there. The
          // canvas it is drawn on is the type's own picture.
          geometry = null,
          region_id = null,
        }
      ]);

      var row = new DBDeviceDiagram
      {
        object_id = Utils.ConvertObjectIdToGuid(objectId)!.Value,
        source_kind = sourceKind,
        source_id = sourceId,
        slot = slot,
        diagram_type_id = typeId,
        auto_created = autoCreated,
      };
      _db.DeviceDiagrams.Add(row);
      await _db.SaveChangesAsync(ct);
      return row;
    }

    /// <summary>
    /// Brings the diagram's children in line with the template and the configured parts. Rows the
    /// service did not make are not in <c>device_diagram_parts</c> and are not touched.
    /// </summary>
    private async Task ReconcileAsync(
      DBDeviceDiagram row, string cameraId, IReadOnlyList<ConfiguredPart> parts, CancellationToken ct)
    {
      if (row.diagram_type_id is not { } typeId)
        return;

      var rules = await _db.DiagramTypeParts.AsNoTracking()
        .Where(p => p.diagram_type_id == typeId)
        .Select(p => new RegionPartRule(p.region_key, p.part_kind, p.match_by, p.match_value))
        .ToListAsync(ct);

      var binding = DiagramTemplateBinding.Bind(rules, parts.Select(p => p.Ref).ToList());

      foreach (var warning in binding.Warnings)
        _logger.LogWarning(
          "Camera {CameraId}, diagram type {TypeId}: {Warning}", cameraId, typeId, warning);

      // Keyed by the part's node, which is what the registry stores: the binding names a part by
      // kind and token, its replica and its row by the node that stands for it.
      var nodesByKey = parts.ToDictionary(p => PartKey(p.Ref), p => p, StringComparer.Ordinal);
      var desired = binding.Bound
        .Where(b => nodesByKey.ContainsKey(PartKey(b.Part)))
        .ToDictionary(b => nodesByKey[PartKey(b.Part)].ObjectId, b => b.RegionKey, StringComparer.Ordinal);

      var managed = await _db.DeviceDiagramParts
        .Where(p => p.diagram_object_id == row.object_id)
        .ToListAsync(ct);

      // A part that is no longer bound loses its replica and its row. A part that merely moved to
      // another region keeps its replica: only the registry row and the placement change, so a
      // child somebody hung under that replica survives a template edit.
      var byPart = managed.ToDictionary(
        m => Utils.ConvertGuidToObjectId(m.part_object_id), StringComparer.Ordinal);

      var dropped = managed
        .Where(m => !desired.ContainsKey(Utils.ConvertGuidToObjectId(m.part_object_id)))
        .ToList();

      if (dropped.Count > 0)
      {
        await _map.RemoveAsync(dropped
          .Select(m => Utils.ConvertGuidToObjectId(m.replica_object_id))
          .ToList());
        _db.DeviceDiagramParts.RemoveRange(dropped);
        await _db.SaveChangesAsync(ct);
      }

      var kept = byPart
        .Where(pair => !dropped.Any(d => d.part_object_id == Utils.ConvertObjectIdToGuid(pair.Key)))
        .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);

      foreach (var (partObjectId, regionKey) in desired)
      {
        var part = parts.First(p => string.Equals(p.ObjectId, partObjectId, StringComparison.Ordinal));

        if (kept.TryGetValue(partObjectId, out var existing))
        {
          if (!string.Equals(existing.region_key, regionKey, StringComparison.Ordinal))
          {
            // The region is part of the registry's key, so the row is rewritten rather than
            // edited; the replica it points at is the same node.
            _db.DeviceDiagramParts.Remove(existing);
            _db.DeviceDiagramParts.Add(new DBDeviceDiagramPart
            {
              diagram_object_id = existing.diagram_object_id,
              region_key = regionKey,
              part_object_id = existing.part_object_id,
              replica_object_id = existing.replica_object_id,
            });
          }

          await EnsureReplicaAsync(row, existing, regionKey, part, ct);
          continue;
        }

        var replicaId = await CreateReplicaAsync(row, regionKey, part, ct);
        _db.DeviceDiagramParts.Add(new DBDeviceDiagramPart
        {
          diagram_object_id = row.object_id,
          region_key = regionKey,
          part_object_id = Utils.ConvertObjectIdToGuid(part.ObjectId)!.Value,
          replica_object_id = Utils.ConvertObjectIdToGuid(replicaId)!.Value,
        });
      }

      await _db.SaveChangesAsync(ct);
    }

    private static string PartKey(DevicePartRef part) => $"{part.Kind}:{part.Token}";

    private async Task<string> CreateReplicaAsync(
      DBDeviceDiagram row, string regionKey, ConfiguredPart part, CancellationToken ct)
    {
      var diagramId = Utils.ConvertGuidToObjectId(row.object_id);

      var created = await _updates.UpdateObjects([
        new BaseMarkerDTO
        {
          parent_id = diagramId,
          owner_id = part.ObjectId,
          name = part.DisplayName,
        }
      ]);

      var replicaId = created[0].id!;
      await _diagrams.UpdateListAsync([
        new DiagramDTO { id = replicaId, geometry = null, region_id = regionKey }
      ]);

      return replicaId;
    }

    /// <summary>
    /// Corrects a replica that is still the same part in the same region, writing only what
    /// differs: this runs for every part of every camera on every sweep, and an unchanged write
    /// would show up in every open map as a change.
    /// </summary>
    private async Task EnsureReplicaAsync(
      DBDeviceDiagram row, DBDeviceDiagramPart managed, string regionKey, ConfiguredPart part,
      CancellationToken ct)
    {
      var replicaId = Utils.ConvertGuidToObjectId(managed.replica_object_id);
      var diagramId = Utils.ConvertGuidToObjectId(row.object_id);
      var replicaGuid = managed.replica_object_id;

      var marker = await _db.Markers.AsNoTracking().FirstOrDefaultAsync(m => m.id == replicaGuid, ct);
      var parentGuid = Utils.ConvertObjectIdToGuid(diagramId);

      if (marker is null || marker.name != part.DisplayName || marker.parent_id != parentGuid)
      {
        await _updates.UpdateObjects([
          new BaseMarkerDTO
          {
            id = replicaId,
            parent_id = diagramId,
            owner_id = part.ObjectId,
            name = part.DisplayName,
          }
        ]);
      }

      var placement = await _db.Diagrams.AsNoTracking().FirstOrDefaultAsync(
        d => d.id == replicaGuid, ct);

      if (placement is null || !string.Equals(placement.region_id, regionKey, StringComparison.Ordinal))
      {
        await _diagrams.UpdateListAsync([
          new DiagramDTO { id = replicaId, geometry = null, region_id = regionKey }
        ]);
      }
    }

    private bool IsSupported(string sourceKind, string sourceId) =>
      string.Equals(sourceKind, DeviceObjectId.CameraSource, StringComparison.Ordinal)
      && !string.IsNullOrWhiteSpace(sourceId);

    private Task<Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction?> OwnTransactionAsync(
      CancellationToken ct) =>
      _db.Database.IsRelational() && _db.Database.CurrentTransaction is null
        ? _db.Database.BeginTransactionAsync(ct)
        : Task.FromResult<Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction?>(null);
  }
}
