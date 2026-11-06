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

    public async Task<HashSet<string>> AllowedAsync(
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

      var original = new Dictionary<Guid, string>();
      foreach (var id in requested)
      {
        var guid = Utils.ConvertObjectIdToGuid(id);
        if (guid is null)
          continue;
        original.TryAdd(guid.Value, id);
      }

      if (original.Count == 0)
        return result;

      var mode = await GetModeAsync(ct);
      var subjects = caller.Subjects.ToList();

      var allowed = await WalkAsync(original.Keys.ToHashSet(), action, subjects, mode, ct);
      foreach (var (guid, id) in original)
      {
        if (allowed.Contains(guid))
          result.Add(id);
      }

      // A device node stands under its server, where the rights granted on the space it is drawn
      // in cannot reach it. Its reference replica stands in that space, so the device is allowed
      // whatever the replica is: the walk above cannot see the space, this one can. A device with
      // no reference replica has only the path above, which ends at its server's node -- exactly
      // the anchor the model names for it.
      var anchors = await ReferenceAnchorsAsync(original.Keys.ToHashSet(), ct);
      if (anchors.Count > 0)
      {
        var anchorAllowed = await WalkAsync(anchors.Values.ToHashSet(), action, subjects, mode, ct);
        foreach (var (node, anchor) in anchors)
        {
          if (anchorAllowed.Contains(anchor))
            result.Add(original[node]);
        }
      }

      return result;
    }

    /// <summary>
    /// The reference replica of every requested device node: the node whose space a device's rights
    /// belong to. A part is a device row too, and resolves through the same camera id.
    /// </summary>
    private async Task<Dictionary<Guid, Guid>> ReferenceAnchorsAsync(
      HashSet<Guid> requested,
      CancellationToken ct)
    {
      var rows = await (from node in _db.DeviceNodes.AsNoTracking()
                        join placement in _db.CameraPlacements.AsNoTracking()
                          on node.source_id equals placement.camera_id
                        where requested.Contains(node.object_id) && placement.is_reference
                        select new { node.object_id, anchor = placement.object_id }).ToListAsync(ct);

      return rows.ToDictionary(row => row.object_id, row => row.anchor);
    }

    /// <summary>
    /// Which of <paramref name="roots"/> are allowed: a row on the node or on any ancestor, through
    /// parents and replica owners. In open mode a node with no row anywhere above it is open, while
    /// a row above it closes it even when it names somebody else.
    /// </summary>
    private async Task<HashSet<Guid>> WalkAsync(
      HashSet<Guid> roots,
      string action,
      IReadOnlyList<string> subjects,
      RightsMode mode,
      CancellationToken ct)
    {
      var states = roots.ToDictionary(guid => guid, guid => new RootState(guid));

      while (states.Values.Any(state => state.Frontier.Count > 0))
      {
        var current = states.Values
          .SelectMany(state => state.Frontier)
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

        foreach (var state in states.Values)
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

      var allowed = new HashSet<Guid>();
      foreach (var (root, state) in states)
      {
        if (!state.Exists)
          continue;

        if (state.Granted || (!state.HasRows && mode == RightsMode.Open))
          allowed.Add(root);
      }

      return allowed;
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
      // And the other way round: a right that reached a picture reached the camera. That is how a
      // right granted on a space -- where only a replica stands -- arrives at the canonical node
      // under the server, and from there at the camera's parts.
      var owners = objects
        .Where(o => o.owner_id.HasValue)
        .ToDictionary(o => o.id, o => o.owner_id!.Value);

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
          .Concat(replicas.GetValueOrDefault(node, []))
          .Concat(owners.TryGetValue(node, out var owner) ? [owner] : []);

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
