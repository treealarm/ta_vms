using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using VmsMap.Domain;

namespace VmsMap.Db
{
  /// A replica is a picture of another node on a canvas: it has no states, alarms or values of its
  /// own and shows its owner's, under its own id. The reads and the watch stream must answer the
  /// same way, or a replica would repaint on a reload and stand still while the operator watches.
  internal static class OwnerView
  {
    public static async Task<Dictionary<string, ObjectStateDTO>> StatesAsync(
      IMapService map, IStateService states, IReadOnlyCollection<string> ids)
    {
      var (owners, ownersAndViews) = await OwnersAsync(map, ids);
      var own = await states.GetStatesAsync(owners);

      var result = new Dictionary<string, ObjectStateDTO>(StringComparer.Ordinal);
      foreach (var id in ids)
      {
        if (own.TryGetValue(id, out var state))
        {
          result[id] = state;
          continue;
        }

        // The owner's version travels with the replica: the watch diff is what notices the change.
        if (ownersAndViews.TryGetValue(id, out var view) &&
            !string.IsNullOrEmpty(view.owner_id) &&
            own.TryGetValue(view.owner_id, out var ownerState))
        {
          result[id] = ownerState with { id = id };
        }
      }

      return result;
    }

    public static async Task<Dictionary<string, AlarmState>> AlarmsAsync(
      IMapService map, IStateService states, IReadOnlyCollection<string> ids)
    {
      var (owners, ownersAndViews) = await OwnersAsync(map, ids);
      var own = await states.GetAlarmStatesAsync(owners);

      var result = new Dictionary<string, AlarmState>(StringComparer.Ordinal);
      foreach (var id in ids)
      {
        if (own.TryGetValue(id, out var alarm))
        {
          result[id] = alarm;
          continue;
        }

        if (ownersAndViews.TryGetValue(id, out var view) &&
            !string.IsNullOrEmpty(view.owner_id) &&
            own.TryGetValue(view.owner_id, out var ownerState))
        {
          result[id] = ownerState with { id = id };
        }
      }

      return result;
    }

    /// The ids that own their rows — everyone else answers through `owner_id` — and the map that
    /// says which is which. One read of the tree per call, whichever of the two answers is asked.
    private static async Task<(List<string> Owners, Dictionary<string, BaseMarkerDTO> Views)> OwnersAsync(
      IMapService map, IReadOnlyCollection<string> ids)
    {
      var ownersAndViews = await map.GetOwnersAndViewsAsync(ids.ToList());
      var owners = ownersAndViews
        .Where(kv => string.IsNullOrEmpty(kv.Value.owner_id))
        .Select(kv => kv.Key)
        .ToList();

      return (owners, ownersAndViews);
    }
  }
}
