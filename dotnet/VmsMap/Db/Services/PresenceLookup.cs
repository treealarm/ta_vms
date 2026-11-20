using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using VmsMap.Domain;

namespace VmsMap.Db
{
  // The presence of a device part, as its device's last successful manifest stated it. A part is
  // the only node this answers for: the camera's own row says the device answered, not that one of
  // its parts is there, and a space or a plain figure has no device behind it. A replica answers
  // with its owner's part, exactly as CameraIdLookup resolves the camera of a picture.
  internal static class PresenceLookup
  {
    /// <summary>
    /// The requested ids that name a device part, or a replica of one, with the part's presence.
    /// An id with no device part behind it is left out, so a caller can tell "not a part" from
    /// "a part that is absent".
    /// </summary>
    public static async Task<Dictionary<string, bool>> ResolveAsync(
      VmsMapDbContext db, IEnumerable<string> ids, CancellationToken ct)
    {
      var requested = ids
        .Where(id => !string.IsNullOrWhiteSpace(id))
        .Distinct(StringComparer.Ordinal)
        .ToList();
      var guids = ToGuids(requested);

      var result = new Dictionary<string, bool>(StringComparer.Ordinal);
      if (guids.Count == 0)
        return result;

      // Owners are read in the same pass: a replica created between placements has only its
      // owner's device_node row to answer with.
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

      var parts = new Dictionary<Guid, bool>();
      foreach (var row in await db.DeviceNodes.AsNoTracking()
        .Where(n => allGuids.Contains(n.object_id)
                    && n.part_kind != DeviceObjectId.DevicePart)
        .Select(n => new { n.object_id, n.present })
        .ToListAsync(ct))
      {
        parts[row.object_id] = row.present;
      }

      foreach (var id in requested)
      {
        var guid = Utils.ConvertObjectIdToGuid(id);
        if (guid is null)
          continue;

        if (parts.TryGetValue(guid.Value, out var own))
        {
          result[id] = own;
        }
        else if (ownerOf.TryGetValue(id, out var ownerId)
                 && Utils.ConvertObjectIdToGuid(ownerId) is { } ownerGuid
                 && parts.TryGetValue(ownerGuid, out var inherited))
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
