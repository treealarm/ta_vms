using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using VmsMap.Domain;

namespace VmsMap.Db
{
  // Rights on the tree, inherited downwards. A right is a row on a node; a node is allowed an
  // action when the row names the caller (user name or one of their roles) on the node itself or
  // on any ancestor. A replica also follows its owner, because a picture of a camera is the same
  // camera on another plan and a right on the camera must cover it.
  //
  // The tree is walked level by level instead of with a recursive CTE: the depth is the number of
  // queries, and the same code runs on the in-memory provider the tests use.
  public sealed class RightsService : IRightsService
  {
    private const string OpenMode = "open";
    private const string EnforcedMode = "enforced";

    private readonly VmsMapDbContext _db;

    public RightsService(VmsMapDbContext db)
    {
      _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    public Task<HashSet<string>> VisibleAsync(
      IReadOnlyCollection<string> objectIds, CallerContext caller, CancellationToken ct) =>
      AllowedAsync(objectIds, RightActions.View, caller, ct);

    public async Task<bool> IsAllowedAsync(
      string objectId, string action, CallerContext caller, CancellationToken ct)
    {
      var allowed = await AllowedAsync([objectId], action, caller, ct);
      return allowed.Contains(objectId);
    }

    private async Task<HashSet<string>> AllowedAsync(
      IReadOnlyCollection<string> objectIds,
      string action,
      CallerContext caller,
      CancellationToken ct)
    {
      var requested = objectIds
        .Where(id => !string.IsNullOrWhiteSpace(id))
        .Distinct(StringComparer.Ordinal)
        .ToList();

      var result = new HashSet<string>(StringComparer.Ordinal);
      if (requested.Count == 0)
        return result;

      // The admin role bypasses every row, as the old camera ACL did.
      if (caller.IsAdmin)
      {
        result.UnionWith(requested);
        return result;
      }

      var roots = new Dictionary<Guid, RootState>();
      var original = new Dictionary<Guid, string>();
      foreach (var id in requested)
      {
        var guid = Utils.ConvertObjectIdToGuid(id);
        if (guid is null || !original.TryAdd(guid.Value, id))
          continue;

        roots[guid.Value] = new RootState(guid.Value);
      }

      if (roots.Count == 0)
        return result;

      var mode = await GetModeAsync(ct);
      var subjects = caller.Subjects.ToList();

      while (roots.Values.Any(r => r.Frontier.Count > 0))
      {
        var current = roots.Values
          .SelectMany(r => r.Frontier)
          .Distinct()
          .ToList();

        var markers = await _db.Markers.AsNoTracking()
          .Where(m => current.Contains(m.id))
          .Select(m => new { m.id, m.parent_id, m.owner_id })
          .ToListAsync(ct);
        var byId = markers.ToDictionary(m => m.id);

        var rows = await _db.Rights.AsNoTracking()
          .Where(r => current.Contains(r.object_id))
          .Select(r => new { r.object_id, r.subject, r.action })
          .ToListAsync(ct);
        var byNode = rows
          .GroupBy(r => r.object_id)
          .ToDictionary(g => g.Key, g => g.ToList());

        foreach (var state in roots.Values)
        {
          var next = new HashSet<Guid>();

          foreach (var node in state.Frontier)
          {
            if (!byId.TryGetValue(node, out var marker))
              continue;

            if (node == state.Root)
              state.Exists = true;

            if (byNode.TryGetValue(node, out var nodeRows))
            {
              state.HasRows |= nodeRows.Count > 0;
              state.Granted |= nodeRows.Any(r =>
                r.action == action && subjects.Contains(r.subject, StringComparer.Ordinal));
            }

            if (marker.parent_id is Guid parent && state.Visited.Add(parent))
              next.Add(parent);

            // A picture of a camera is the same camera: a right on the owner covers it too.
            if (marker.owner_id is Guid owner && state.Visited.Add(owner))
              next.Add(owner);
          }

          state.Frontier = next;
        }
      }

      foreach (var (root, state) in roots)
      {
        if (!state.Exists)
          continue;

        // In open mode a node with no rights row anywhere above it is open to everyone. A row
        // anywhere closes it, even when that row names somebody else: the node inherited a
        // restricted set rather than no set at all.
        if (state.Granted || (!state.HasRows && mode == RightsMode.Open))
          result.Add(original[root]);
      }

      return result;
    }

    public async Task<List<NodeRightsDTO>> GetEffectiveAsync(
      CallerContext caller, CancellationToken ct)
    {
      if (caller.IsAdmin)
        return [];

      var subjects = caller.Subjects.ToList();
      if (subjects.Count == 0)
        return [];

      var rows = await _db.Rights.AsNoTracking()
        .Where(r => subjects.Contains(r.subject))
        .ToListAsync(ct);

      if (rows.Count == 0)
        return [];

      // One query for the whole tree: the explicit grants are expanded down it once per call, and
      // web_srv caches the answer for a minute.
      var objects = await _db.Markers.AsNoTracking()
        .Select(m => new { m.id, m.parent_id, m.owner_id })
        .ToListAsync(ct);
      var children = objects
        .Where(o => o.parent_id.HasValue)
        .GroupBy(o => o.parent_id!.Value)
        .ToDictionary(g => g.Key, g => g.Select(o => o.id).ToList());
      // A picture of a camera inherits the camera's rights, wherever on the tree the camera node
      // itself stands.
      var replicas = objects
        .Where(o => o.owner_id.HasValue)
        .GroupBy(o => o.owner_id!.Value)
        .ToDictionary(g => g.Key, g => g.Select(o => o.id).ToList());

      var actions = new Dictionary<Guid, HashSet<string>>();
      var queue = new Queue<Guid>();

      foreach (var group in rows.GroupBy(r => r.object_id))
      {
        actions[group.Key] = new HashSet<string>(
          group.Select(r => r.action), StringComparer.Ordinal);
        queue.Enqueue(group.Key);
      }

      // Actions flow down the tree; a node reached by several paths keeps the union. Enqueuing a
      // child only when its set grew also makes a cycle in parent_id harmless.
      while (queue.Count > 0)
      {
        var node = queue.Dequeue();
        var inherited = children.GetValueOrDefault(node, [])
          .Concat(replicas.GetValueOrDefault(node, []));

        var parentActions = actions[node];
        foreach (var child in inherited)
        {
          if (!actions.TryGetValue(child, out var childActions))
          {
            childActions = new HashSet<string>(StringComparer.Ordinal);
            actions[child] = childActions;
          }

          var grew = false;
          foreach (var action in parentActions)
            grew |= childActions.Add(action);

          if (grew)
            queue.Enqueue(child);
        }
      }

      var ids = actions.Keys.ToList();

      // A camera node's row is in device_node; a picture is named by its placement row.
      var cameras = new Dictionary<Guid, string>();
      foreach (var row in await _db.DeviceNodes.AsNoTracking()
        .Where(n => ids.Contains(n.object_id)
                    && n.source_kind == DeviceObjectId.CameraSource
                    && n.part_kind == DeviceObjectId.DevicePart)
        .Select(n => new { n.object_id, n.source_id })
        .ToListAsync(ct))
      {
        cameras[row.object_id] = row.source_id;
      }
      foreach (var row in await _db.CameraPlacements.AsNoTracking()
        .Where(p => ids.Contains(p.object_id))
        .Select(p => new { p.object_id, p.camera_id })
        .ToListAsync(ct))
      {
        cameras.TryAdd(row.object_id, row.camera_id);
      }

      return actions
        .Select(kv => new NodeRightsDTO
        {
          object_id = Utils.ConvertGuidToObjectId(kv.Key),
          camera_id = cameras.GetValueOrDefault(kv.Key),
          actions = kv.Value.OrderBy(a => a, StringComparer.Ordinal).ToList(),
        })
        .OrderBy(n => n.object_id, StringComparer.Ordinal)
        .ToList();
    }

    public async Task<RightsMode> GetModeAsync(CancellationToken ct)
    {
      var row = await _db.RightsModes.AsNoTracking().FirstOrDefaultAsync(ct);

      // A database that has not been migrated to 0003 yet, or a row somebody removed by hand,
      // must not silently open every camera: enforced is the safe reading.
      return row?.mode == OpenMode ? RightsMode.Open : RightsMode.Enforced;
    }

    public async Task SetModeAsync(RightsMode mode, CancellationToken ct)
    {
      var value = mode == RightsMode.Open ? OpenMode : EnforcedMode;

      var row = await _db.RightsModes.FirstOrDefaultAsync(ct);
      if (row is null)
        _db.RightsModes.Add(new DBRightsMode { id = true, mode = value });
      else
        row.mode = value;

      await _db.SaveChangesAsync(ct);
    }

    private sealed class RootState(Guid root)
    {
      public Guid Root { get; } = root;
      public bool Exists { get; set; }
      public bool HasRows { get; set; }
      public bool Granted { get; set; }
      public HashSet<Guid> Frontier { get; set; } = [root];
      public HashSet<Guid> Visited { get; } = [root];
    }
  }
}
