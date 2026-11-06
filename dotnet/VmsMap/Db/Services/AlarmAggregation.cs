using System;
using System.Collections.Generic;
using System.Linq;

namespace VmsMap.Db
{
  // One node of the part of the tree an alarm update touches: the changed objects, their
  // ancestors, and the direct children of those ancestors (a parent's counter counts its
  // children, so the children outside the update must be visible too).
  internal readonly record struct AlarmNode(
    Guid Id,
    Guid? ParentId,
    bool OwnAlarm,
    bool StoredAlarm,
    int StoredChildren);

  // Recomputes alarms from the tables instead of keeping a counter: an object is alarmed by its
  // own states, and children_alarms is the number of its direct children that are alarmed (own
  // flag or their own children). Everything below the closure that did not change is taken from
  // its stored row, so one update only has to touch the path to the root.
  //
  // Square's actors moved a delta up the tree and lost it whenever the counter changed without
  // the aggregate flag changing, or when an actor was deactivated. A recomputation cannot drift.
  internal static class AlarmAggregation
  {
    /// <param name="externalAlarmedChildren">
    /// How many direct children of each node are alarmed but lie outside the closure. The database
    /// counts them; loading the rows to count them here is what made a server with 100 000 cameras
    /// pay for all of them on every update.
    /// </param>
    public static Dictionary<Guid, (bool Alarm, int Children)> Compute(
      IReadOnlyCollection<AlarmNode> nodes,
      IReadOnlySet<Guid> closure,
      IReadOnlyDictionary<Guid, int>? externalAlarmedChildren = null)
    {
      var byId = nodes.ToDictionary(n => n.Id);

      var children = nodes
        .Where(n => n.ParentId.HasValue && byId.ContainsKey(n.ParentId.Value))
        .GroupBy(n => n.ParentId!.Value)
        .ToDictionary(g => g.Key, g => g.Select(n => n.Id).ToList());

      var depth = ComputeDepths(nodes, byId);

      var computed = new Dictionary<Guid, (bool Alarm, int Children)>(closure.Count);

      // Deepest first: a node's counter needs its children's results, and those are nearer the
      // leaves than it is.
      foreach (var node in nodes.OrderByDescending(n => depth[n.Id]))
      {
        if (!closure.Contains(node.Id))
        {
          continue;
        }

        var alarmedChildren = externalAlarmedChildren?.GetValueOrDefault(node.Id) ?? 0;
        if (children.TryGetValue(node.Id, out var kids))
        {
          foreach (var kid in kids)
          {
            var alarmed = computed.TryGetValue(kid, out var done)
              ? done.Alarm || done.Children > 0
              : IsAlarmed(byId[kid]);

            if (alarmed)
            {
              alarmedChildren++;
            }
          }
        }

        computed[node.Id] = (node.OwnAlarm, alarmedChildren);
      }

      return computed
        .Where(kv =>
        {
          var stored = byId[kv.Key];
          return kv.Value.Alarm != stored.StoredAlarm
              || kv.Value.Children != stored.StoredChildren;
        })
        .ToDictionary(kv => kv.Key, kv => kv.Value);
    }

    private static bool IsAlarmed(AlarmNode node) =>
      node.OwnAlarm || node.StoredAlarm || node.StoredChildren > 0;

    private static Dictionary<Guid, int> ComputeDepths(
      IReadOnlyCollection<AlarmNode> nodes,
      IReadOnlyDictionary<Guid, AlarmNode> byId)
    {
      var depth = new Dictionary<Guid, int>(nodes.Count);
      var visiting = new HashSet<Guid>();

      int DepthOf(Guid id)
      {
        if (depth.TryGetValue(id, out var known))
        {
          return known;
        }

        if (!visiting.Add(id))
        {
          throw new InvalidOperationException(
            $"objects.parent_id contains a cycle at {id}");
        }

        var node = byId[id];
        var d = node.ParentId.HasValue && byId.ContainsKey(node.ParentId.Value)
          ? DepthOf(node.ParentId.Value) + 1
          : 0;

        visiting.Remove(id);
        depth[id] = d;
        return d;
      }

      foreach (var id in byId.Keys)
      {
        DepthOf(id);
      }

      return depth;
    }
  }
}
