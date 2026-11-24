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
  /// Keeps a device's diagram in step with its template and the parts the operator configured: the
  /// type that describes the device now, and one managed replica per configured part, in the region
  /// the template binds it to.
  ///
  /// A step, not a scenario: the caller owns the transaction and the locks (see
  /// <see cref="DeviceDiagramService"/>), so attaching, binding and the periodic sweep all reach
  /// the same decisions under the same serialisation. It never creates a diagram — a diagram is a
  /// map object, and where it stands is the administrator's to say.
  /// </summary>
  internal sealed class DeviceDiagramReconciler
  {
    private readonly VmsMapDbContext _db;
    private readonly IMapService _map;
    private readonly IMapUpdateService _updates;
    private readonly IDiagramServiceInternal _diagrams;
    private readonly IDeviceSettingsSource _settings;
    private readonly ILogger<DeviceDiagramReconciler> _logger;

    public DeviceDiagramReconciler(
      VmsMapDbContext db,
      IMapService map,
      IMapUpdateService updates,
      IDiagramServiceInternal diagrams,
      IDeviceSettingsSource settings,
      ILogger<DeviceDiagramReconciler> logger)
    {
      _db = db ?? throw new ArgumentNullException(nameof(db));
      _map = map ?? throw new ArgumentNullException(nameof(map));
      _updates = updates ?? throw new ArgumentNullException(nameof(updates));
      _diagrams = diagrams ?? throw new ArgumentNullException(nameof(diagrams));
      _settings = settings ?? throw new ArgumentNullException(nameof(settings));
      _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public sealed record ConfiguredPart(string ObjectId, DevicePartRef Ref, string DisplayName);

    /// <summary>
    /// Whether the diagram already says what it should — asked without a transaction, so the
    /// sweep over every camera costs reads and nothing else. A device with no diagram is settled:
    /// a diagram is a map object and the administrator is the one who places it.
    /// </summary>
    public async Task<bool> IsSettledAsync(
      string sourceKind, string sourceId, string slot, CancellationToken ct)
    {
      var row = await _db.DeviceDiagrams.AsNoTracking().FirstOrDefaultAsync(
        d => d.source_kind == sourceKind && d.source_id == sourceId && d.slot == slot, ct);

      if (row is null)
        return true;

      // Deleting a type leaves the canvas where it is, and the service stops maintaining it — but
      // a template for the device coming back is the same null: a demo reinstall deletes the type
      // and creates it again under a new id. So a diagram without a type is settled only while
      // nothing fits the device; once something does, the locks are worth taking to adopt it.
      if (row.diagram_type_id is null)
        return (await SelectModelTypeAsync(sourceId, ct)).TypeId is null;

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

    /// <summary>
    /// Brings the device's canonical diagram in line: the type that describes it now, then its
    /// managed replicas. Runs inside the caller's transaction and locks.
    /// </summary>
    public async Task ReconcileAsync(
      string sourceKind, string sourceId, string slot, CancellationToken ct)
    {
      var row = await _db.DeviceDiagrams.FirstOrDefaultAsync(
        d => d.source_kind == sourceKind && d.source_id == sourceId && d.slot == slot, ct);

      if (row is null)
        // Nothing to maintain: a diagram is a map object, and the administrator places one.
        return;

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

      await ReconcilePartsAsync(row, sourceId, await ConfiguredPartsAsync(sourceId, ct), ct);
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
      var selection = info is null ? Never : await SelectModelTypeAsync(info, ct);

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

    /// <summary>
    /// How one template stands to one device. A named template is the administrator's decision, so
    /// it is refused unless it draws this device — and a device whose model is not known yet cannot
    /// vouch for one: the caller binds without a template and reconciliation takes the matching one
    /// once the model arrives.
    /// </summary>
    public enum TemplateFit
    {
      Missing,
      OtherKind,
      ModelUnknown,
      OtherModel,
      Fits,
    }

    public async Task<TemplateFit> FitAsync(Guid typeId, string cameraId, CancellationToken ct)
    {
      var type = await _db.DiagramTypes.AsNoTracking().FirstOrDefaultAsync(t => t.id == typeId, ct);
      if (type is null)
        return TemplateFit.Missing;

      var info = await DeviceInfoAsync(cameraId, ct);
      if (info is null)
        return TemplateFit.ModelUnknown;

      if (!string.Equals(type.source_kind, info.SourceKind, StringComparison.Ordinal))
        return TemplateFit.OtherKind;

      return DiagramModelMatch.Matches(
        Candidate(type), info.SourceKind, info.Manufacturer, info.Model)
        ? TemplateFit.Fits
        : TemplateFit.OtherModel;
    }

    private static readonly DiagramModelSelection Never = new(null, null);

    public async Task<DiagramModelSelection> SelectModelTypeAsync(
      string cameraId, CancellationToken ct)
    {
      var info = await DeviceInfoAsync(cameraId, ct);
      return info is null ? Never : await SelectModelTypeAsync(info, ct);
    }

    private async Task<DiagramModelSelection> SelectModelTypeAsync(
      DeviceModel info, CancellationToken ct)
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
    public async Task<List<ConfiguredPart>> ConfiguredPartsAsync(
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
    /// what a template change is about. Its place and its own picture stay: a template change is
    /// not a move, and the row is rewritten whole, so what must survive is read first.
    /// </summary>
    public async Task SetTypeAsync(DBDeviceDiagram row, Guid typeId, CancellationToken ct)
    {
      row.diagram_type_id = typeId;

      var id = Utils.ConvertGuidToObjectId(row.object_id);
      var current = await _db.Diagrams.AsNoTracking()
        .FirstOrDefaultAsync(d => d.id == row.object_id, ct);
      var dto = current is null ? new DiagramDTO { id = id } : DiagramService.ToDto(current);
      dto.id = id;
      dto.dgr_type = Utils.ConvertGuidToObjectId(typeId);

      await _diagrams.UpdateListAsync([dto]);
    }

    /// <summary>
    /// Brings the diagram's children in line with the template and the configured parts. Rows the
    /// service did not make are not in <c>device_diagram_parts</c> and are not touched.
    /// </summary>
    public async Task ReconcilePartsAsync(
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
        // The registry rows go before the nodes: a replica's foreign keys cascade into this table,
        // so deleting it first would take the rows along and leave these deletes matching nothing.
        _db.DeviceDiagramParts.RemoveRange(dropped);
        await _db.SaveChangesAsync(ct);
        // The map's own deletion, not a bare node removal: it also recomputes the alarm state of
        // what the replica hung under, which a direct `RemoveAsync` leaves as it was.
        await _updates.Delete(dropped
          .Select(m => Utils.ConvertGuidToObjectId(m.replica_object_id))
          .ToList());
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
  }
}
