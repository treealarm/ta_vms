using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using VmsMap.Db;
using VmsMap.Domain;

namespace VmsMap.Grpc
{
  /// <summary>One place a diagram type is shown: a diagram's own node or one of its replicas.</summary>
  internal sealed record DiagramTypeUsageRow(
    string DiagramId,
    string Name,
    string CanvasId,
    string CanvasName,
    bool IsRepresentation,
    bool IsDevice,
    string SourceKind,
    string SourceId);

  /// <summary>
  /// Where a type is in use, read on every call from the two columns that name it —
  /// <c>diagrams.dgr_type</c> and <c>device_diagram.diagram_type_id</c>. Nothing is stored: a list
  /// kept beside them could drift, and a stale answer here would be worse than a slow one.
  /// </summary>
  internal static class DiagramTypeUsage
  {
    public static async Task<List<DiagramTypeUsageRow>> ResolveAsync(
      VmsMapDbContext db, Guid typeId, CancellationToken ct)
    {
      var drawn = await db.Diagrams.AsNoTracking()
          .Where(d => d.dgr_type == typeId)
          .Select(d => d.id)
          .ToListAsync(ct);

      var bound = await db.DeviceDiagrams.AsNoTracking()
          .Where(d => d.diagram_type_id == typeId)
          .Select(d => new { d.object_id, d.source_kind, d.source_id })
          .ToListAsync(ct);

      // A device's diagram is drawn from the type too, so it is usually in both; the binding is
      // what decides its group and what names the device.
      var canonical = drawn.Union(bound.Select(b => b.object_id)).ToList();
      if (canonical.Count == 0)
        return [];

      // Every place the diagram is shown, not only the diagram: a replica names the original one
      // level deep, so one query finds them all. A row whose node is gone is not a diagram
      // anybody can see and answers for nothing.
      var placements = await db.Markers.AsNoTracking()
          .Where(m => canonical.Contains(m.id) ||
                      (m.owner_id != null && canonical.Contains(m.owner_id.Value)))
          .Select(m => new { m.id, m.name, m.owner_id })
          .ToListAsync(ct);
      if (placements.Count == 0)
        return [];

      // The nearest space or mnemoscheme: what the operator would call the place it stands in.
      var canvasOf = await CanvasResolver.ResolveForAsync(
          db, placements.Select(p => p.id).ToList());

      var canvasIds = canvasOf.Values
          .Where(id => id.HasValue)
          .Select(id => id!.Value)
          .Distinct()
          .ToList();
      var canvasNames = canvasIds.Count == 0
          ? new Dictionary<Guid, string>()
          : await db.Markers.AsNoTracking()
              .Where(m => canvasIds.Contains(m.id))
              .Select(m => new { m.id, m.name })
              .ToDictionaryAsync(m => m.id, m => m.name, ct);

      var boundByObject = bound.ToDictionary(b => b.object_id);

      return placements
          .Select(placement =>
          {
            var device = boundByObject.GetValueOrDefault(placement.owner_id ?? placement.id);
            var canvas = canvasOf.TryGetValue(placement.id, out var found) ? found : null;
            return new DiagramTypeUsageRow(
              DiagramId: Utils.ConvertGuidToObjectId(placement.id),
              Name: placement.name ?? string.Empty,
              CanvasId: canvas is { } canvasId ? Utils.ConvertGuidToObjectId(canvasId) : string.Empty,
              CanvasName: canvas is { } named ? canvasNames.GetValueOrDefault(named, string.Empty) : string.Empty,
              IsRepresentation: placement.owner_id != null,
              IsDevice: device is not null,
              SourceKind: device?.source_kind ?? string.Empty,
              SourceId: device?.source_id ?? string.Empty);
          })
          .OrderBy(row => row.IsDevice)
          .ThenBy(row => row.Name, StringComparer.Ordinal)
          .ThenBy(row => row.IsRepresentation)
          .ThenBy(row => row.DiagramId, StringComparer.Ordinal)
          .ToList();
    }
  }
}
