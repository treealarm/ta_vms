using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using VmsMap.Db;
using VmsMap.Domain;

namespace VmsMap.Adapter
{
  /// <summary>The presence states a camera's node carries, and when they were last written.</summary>
  internal sealed record CameraPresenceRow(IReadOnlySet<string> Presence, DateTime? Timestamp);

  // Reads the two pieces of map state the presence sweep needs: which cameras are marked NO_VIDEO,
  // and what the camera nodes carry now. The second is also the staleness guard — a row written
  // after the sweep began observing is a fresher decision and must not be overwritten.
  internal sealed class CameraPresenceStore(VmsMapDbContext db, IStateService stateService)
  {
    private const int BatchSize = 1000;

    private static readonly string[] PresenceStates =
      [CameraStateWriter.Connected, CameraStateWriter.Disconnected, CameraStateWriter.NoVideo];

    public async Task<IReadOnlyList<string>> ListNoVideoCameraIdsAsync(CancellationToken ct)
    {
      var ids = await (
        from state in db.ObjectStates
        join node in db.DeviceNodes on state.id equals node.object_id
        where node.source_kind == DeviceObjectId.CameraSource
              && node.part_kind == DeviceObjectId.DevicePart
              && state.states.Any(value => value.state == CameraStateWriter.NoVideo)
        select node.source_id)
        .Distinct()
        .ToListAsync(ct);

      // The tree id is a one-way hash; device_node is what turns it back into a camera id.
      return ids.Select(id => id.ToString()).ToList();
    }

    public async Task<Dictionary<string, CameraPresenceRow>> LoadAsync(
      IReadOnlyCollection<string> cameraIds, CancellationToken ct)
    {
      var result = new Dictionary<string, CameraPresenceRow>(StringComparer.Ordinal);
      var pairs = cameraIds
        .Select(cameraId => (CameraId: cameraId, ObjectId: CameraObjectId.For(cameraId)))
        .ToList();

      for (var offset = 0; offset < pairs.Count; offset += BatchSize)
      {
        var chunk = pairs.GetRange(offset, Math.Min(BatchSize, pairs.Count - offset));
        var states = await stateService.GetStatesAsync(chunk.Select(p => p.ObjectId).ToList());

        foreach (var pair in chunk)
        {
          if (!states.TryGetValue(pair.ObjectId, out var dto))
            continue; // no row yet: nothing stored, nothing to compare against

          var presence = new HashSet<string>(
            dto.states.Where(PresenceStates.Contains), StringComparer.Ordinal);
          result[pair.CameraId] = new CameraPresenceRow(presence, dto.timestamp);
        }
      }

      return result;
    }
  }
}
