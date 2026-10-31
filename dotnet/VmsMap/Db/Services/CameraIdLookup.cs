using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using VmsMap.Domain;

namespace VmsMap.Db
{
  // The reverse index from a node to the camera it is: camera_node for a canonical camera, the
  // placement row for one of its pictures. Used by every read that has to name a camera and by the
  // watch stream.
  internal static class CameraIdLookup
  {
    public static async Task<Dictionary<string, string>> ResolveAsync(
      VmsMapDbContext db, IEnumerable<string> ids, CancellationToken ct)
    {
      var requested = ids
        .Where(id => !string.IsNullOrWhiteSpace(id))
        .Distinct(StringComparer.Ordinal)
        .ToList();
      var guids = requested
        .Select(Utils.ConvertObjectIdToGuid)
        .Where(g => g.HasValue)
        .Select(g => g!.Value)
        .ToList();

      var result = new Dictionary<string, string>(StringComparer.Ordinal);
      if (guids.Count == 0)
        return result;

      foreach (var row in await db.CameraNodes.AsNoTracking()
        .Where(n => guids.Contains(n.object_id))
        .Select(n => new { n.object_id, n.camera_id })
        .ToListAsync(ct))
      {
        result[Utils.ConvertGuidToObjectId(row.object_id)] = row.camera_id;
      }

      foreach (var row in await db.CameraPlacements.AsNoTracking()
        .Where(p => guids.Contains(p.object_id))
        .Select(p => new { p.object_id, p.camera_id })
        .ToListAsync(ct))
      {
        result.TryAdd(Utils.ConvertGuidToObjectId(row.object_id), row.camera_id);
      }

      return result;
    }
  }
}
