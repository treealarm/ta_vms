using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using VmsMap.Domain;

namespace VmsMap.Db
{
  // What kind of node a tree row is. The tree has no type column: a node is what its side row
  // says it is. `NodeKind` is the rights model's enum, so the browser picks the tree icon and the
  // actions the rights editor offers from the same answer. A replica answers with its owner's
  // kind: a picture of a camera on another plan is still that camera.
  internal static class NodeKindLookup
  {
    public static async Task<Dictionary<string, NodeKind>> ResolveAsync(
      VmsMapDbContext db, IEnumerable<string> ids, CancellationToken ct)
    {
      var requested = ids
        .Where(id => !string.IsNullOrWhiteSpace(id))
        .Distinct(StringComparer.Ordinal)
        .ToList();

      var result = requested.ToDictionary(id => id, _ => NodeKind.Object, StringComparer.Ordinal);
      if (result.Count == 0)
        return result;

      var requestedGuids = ToGuids(requested);

      // A replica carries its owner's kind. The owners are read in the same side-table queries
      // as the requested ids, so this stays one lookup rather than a recursion per node.
      var owners = await db.Markers.AsNoTracking()
        .Where(m => requestedGuids.Contains(m.id) && m.owner_id != null)
        .Select(m => new { m.id, m.owner_id })
        .ToListAsync(ct);
      var ownerOf = owners.ToDictionary(
        o => Utils.ConvertGuidToObjectId(o.id),
        o => o.owner_id!.Value,
        StringComparer.Ordinal);
      var allGuids = requestedGuids
        .Concat(ownerOf.Values)
        .Distinct()
        .ToList();

      // Lowest precedence first: a space also has a footprint in geo_objects and a camera node
      // may too, while only its own side row decides what it is.
      var kinds = new Dictionary<Guid, NodeKind>();
      foreach (var id in await db.GeoObjects.AsNoTracking()
        .Where(g => allGuids.Contains(g.id))
        .Select(g => g.id)
        .ToListAsync(ct))
        kinds[id] = NodeKind.Figure;

      foreach (var id in await CameraGuidsAsync(db, allGuids, ct))
        kinds[id] = NodeKind.Camera;

      foreach (var id in await db.Landmarks.AsNoTracking()
        .Where(l => allGuids.Contains(l.object_id))
        .Select(l => l.object_id)
        .ToListAsync(ct))
        kinds[id] = NodeKind.Landmark;

      foreach (var id in await db.Diagrams.AsNoTracking()
        .Where(d => allGuids.Contains(d.id))
        .Select(d => d.id)
        .ToListAsync(ct))
        kinds[id] = NodeKind.Diagram;

      foreach (var id in await db.Spaces.AsNoTracking()
        .Where(s => allGuids.Contains(s.object_id))
        .Select(s => s.object_id)
        .ToListAsync(ct))
        kinds[id] = NodeKind.Space;

      foreach (var id in requested)
      {
        var guid = Utils.ConvertObjectIdToGuid(id);
        if (guid is not null && kinds.TryGetValue(guid.Value, out var own))
        {
          result[id] = own;
        }
        else if (ownerOf.TryGetValue(id, out var ownerGuid)
                 && kinds.TryGetValue(ownerGuid, out var inherited))
        {
          result[id] = inherited;
        }
      }

      return result;
    }

    public static string Name(NodeKind kind) => RightActions.Name(kind);

    // A canonical camera has a camera_node row; a picture created for a placement has the
    // placement row itself. Both say "this node is a camera".
    private static async Task<List<Guid>> CameraGuidsAsync(
      VmsMapDbContext db, List<Guid> guids, CancellationToken ct)
    {
      var result = await db.CameraNodes.AsNoTracking()
        .Where(n => guids.Contains(n.object_id))
        .Select(n => n.object_id)
        .ToListAsync(ct);

      result.AddRange(await db.CameraPlacements.AsNoTracking()
        .Where(p => guids.Contains(p.object_id))
        .Select(p => p.object_id)
        .ToListAsync(ct));

      return result.Distinct().ToList();
    }

    private static List<Guid> ToGuids(IEnumerable<string> ids) =>
      ids
        .Select(Utils.ConvertObjectIdToGuid)
        .Where(g => g.HasValue)
        .Select(g => g!.Value)
        .Distinct()
        .ToList();
  }
}
