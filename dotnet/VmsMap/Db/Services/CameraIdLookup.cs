using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using VmsMap.Domain;

namespace VmsMap.Db
{
  // The reverse index from a node to the camera it is: the device_node row for a canonical camera,
  // the placement row for one of its pictures. A replica with neither row answers with its owner's
  // camera, because a picture of a camera opens the same live video. A part's node (a digital input,
  // a relay) is not the camera and answers nothing here. Used by every read that has to name a
  // camera and by the watch stream.
  internal static class CameraIdLookup
  {
    public static async Task<Dictionary<string, string>> ResolveAsync(
      VmsMapDbContext db, IEnumerable<string> ids, CancellationToken ct)
    {
      var requested = ids
        .Where(id => !string.IsNullOrWhiteSpace(id))
        .Distinct(StringComparer.Ordinal)
        .ToList();
      var guids = ToGuids(requested);

      var result = new Dictionary<string, string>(StringComparer.Ordinal);
      if (guids.Count == 0)
        return result;

      // Owners are read in the same two queries: a replica created between placements has only
      // its owner's device_node row to answer with.
      var owners = await db.Markers.AsNoTracking()
        .Where(m => guids.Contains(m.id) && m.owner_id != null)
        .Select(m => new { m.id, m.owner_id })
        .ToListAsync(ct);
      var ownerOf = owners.ToDictionary(
        o => Utils.ConvertGuidToObjectId(o.id),
        o => Utils.ConvertGuidToObjectId(o.owner_id!.Value),
        StringComparer.Ordinal);
      var allGuids = guids
        .Concat(owners.Select(o => o.owner_id!.Value))
        .Distinct()
        .ToList();

      var cameras = new Dictionary<Guid, string>();
      foreach (var row in await db.DeviceNodes.AsNoTracking()
        .Where(n => allGuids.Contains(n.object_id)
                    && n.source_kind == DeviceObjectId.CameraSource
                    && n.part_kind == DeviceObjectId.DevicePart)
        .Select(n => new { n.object_id, n.source_id })
        .ToListAsync(ct))
      {
        cameras[row.object_id] = row.source_id;
      }

      foreach (var row in await db.CameraPlacements.AsNoTracking()
        .Where(p => allGuids.Contains(p.object_id))
        .Select(p => new { p.object_id, p.camera_id })
        .ToListAsync(ct))
      {
        cameras.TryAdd(row.object_id, row.camera_id);
      }

      foreach (var id in requested)
      {
        var guid = Utils.ConvertObjectIdToGuid(id);
        if (guid is null)
          continue;

        if (cameras.TryGetValue(guid.Value, out var own))
        {
          result[id] = own;
        }
        else if (ownerOf.TryGetValue(id, out var ownerId)
                 && Utils.ConvertObjectIdToGuid(ownerId) is { } ownerGuid
                 && cameras.TryGetValue(ownerGuid, out var inherited))
        {
          result[id] = inherited;
        }
      }

      return result;
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
