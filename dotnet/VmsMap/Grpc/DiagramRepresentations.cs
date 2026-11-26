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
  /// <summary>
  /// A node's diagram row as the browser must read it, and the diagram it stands for when the node
  /// is a replica of one.
  /// </summary>
  /// <param name="OwnerId">The diagram the node is a representation of; null on an ordinary node.</param>
  /// <param name="Row">
  /// The row the node reports; null when it has none of its own and represents nothing. A picture
  /// of a camera is such a node: it is a replica, but not of a diagram.
  /// </param>
  internal sealed record ResolvedDiagram(string? OwnerId, DiagramDTO? Row);

  /// <summary>
  /// Resolves a node to the diagram it shows. A replica of a diagram has no type, picture or
  /// children of its own: its content is its owner's, and only its place — a region of its parent
  /// or a rectangle of its own — belongs to the replica. The flag says which kind a row is; it is
  /// never inferred from the geometry, because a representation placed in a region carries no
  /// geometry at all.
  ///
  /// One place computes this, for the same reason every read must agree: the diagram's content and
  /// the rows fetched by id cannot tell the client two different things about the same node.
  /// </summary>
  internal static class DiagramRepresentations
  {
    /// <summary>
    /// The canvas a read means. A representation opens on the diagram it shows, and the diagram's
    /// figures belong to the diagram, so reading a copy's canvas reads the diagram's. Anything
    /// else — a space, an ordinary diagram, an unknown id — is its own canvas.
    /// </summary>
    public static async Task<string?> ResolveCanvasAsync(
      VmsMapDbContext db, string? canvasId, CancellationToken ct)
    {
      if (string.IsNullOrWhiteSpace(canvasId))
        return canvasId;

      var resolved = await ResolveAsync(db, [canvasId], ct);
      return resolved.TryGetValue(canvasId, out var diagram) && diagram.OwnerId is { } owner
        ? owner
        : canvasId;
    }

    public static async Task<Dictionary<string, ResolvedDiagram>> ResolveAsync(
      VmsMapDbContext db, IReadOnlyCollection<string> ids, CancellationToken ct)
    {
      var resolved = new Dictionary<string, ResolvedDiagram>(StringComparer.Ordinal);
      if (ids.Count == 0)
        return resolved;

      var guids = ids
        .Select(Utils.ConvertObjectIdToGuid)
        .Where(id => id.HasValue)
        .Select(id => id!.Value)
        .Distinct()
        .ToList();

      // Who each node shows, if anyone: `owner_id` names the original one level deep.
      var owners = guids.Count == 0
        ? new Dictionary<Guid, Guid>()
        : await db.Markers.AsNoTracking()
            .Where(m => guids.Contains(m.id) && m.owner_id != null)
            .Select(m => new { m.id, m.owner_id })
            .ToDictionaryAsync(m => m.id, m => m.owner_id!.Value, ct);

      var ownRows = guids.Count == 0
        ? new Dictionary<Guid, DBDiagram>()
        : await db.Diagrams.AsNoTracking()
            .Where(d => guids.Contains(d.id))
            .ToDictionaryAsync(d => d.id, ct);

      var ownerIds = owners.Values.Distinct().ToList();
      var ownerRows = ownerIds.Count == 0
        ? new Dictionary<Guid, DBDiagram>()
        : await db.Diagrams.AsNoTracking()
            .Where(d => ownerIds.Contains(d.id))
            .ToDictionaryAsync(d => d.id, ct);

      foreach (var id in ids)
      {
        var guid = Utils.ConvertObjectIdToGuid(id);
        ownRows.TryGetValue(guid ?? Guid.Empty, out var own);

        if (guid is { } node && owners.TryGetValue(node, out var owner)
            && ownerRows.TryGetValue(owner, out var ownerRow))
        {
          var row = DiagramService.ToDto(ownerRow);
          row.id = id;
          row.placement = DiagramService.ToDto(own?.geometry);
          row.region_id = own?.region_id;
          row.is_representation = true;

          resolved[id] = new ResolvedDiagram(Utils.ConvertGuidToObjectId(owner), row);
          continue;
        }

        if (own is null)
        {
          resolved[id] = new ResolvedDiagram(null, null);
          continue;
        }

        var ownRow = DiagramService.ToDto(own);
        ownRow.id = id;
        resolved[id] = new ResolvedDiagram(null, ownRow);
      }

      return resolved;
    }
  }
}
