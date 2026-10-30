using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;

namespace VmsMap.Db
{
  // Which canvas a node's figure belongs to: the nearest ancestor that is a space or a
  // mnemoscheme. Coordinates are always the canvas ancestor's, and made-up plans and mnemoschemes
  // both cross (0, 0), so without this a figure cannot be told apart from one on another world.
  //
  // A static helper on purpose: it reads the tree through the caller's DbContext and holds no
  // state, so any write path can use it without another injected dependency. It walks the parent
  // chain level by level for all the given nodes at once -- the depth of the tree, not its size.
  internal static class CanvasResolver
  {
    /// <summary>
    /// The canvas of each node's own figure, walking up from the node's parent. Null for a node
    /// with no canvas ancestor (a root) and for a node that does not exist.
    /// </summary>
    public static async Task<Dictionary<Guid, Guid?>> ResolveForAsync(
      VmsMapDbContext db, IReadOnlyCollection<Guid> objectIds)
    {
      var starts = objectIds.Distinct().ToList();
      var result = starts.ToDictionary(id => id, _ => (Guid?)null);
      if (starts.Count == 0)
        return result;

      // Where each walk currently stands; an entry lives in `pending` only while it has a node.
      var current = starts.ToDictionary(id => id, id => (Guid?)id);
      var visited = starts.ToDictionary(id => id, _ => new HashSet<Guid>());
      var pending = new HashSet<Guid>(starts);

      while (pending.Count > 0)
      {
        var nodeIds = pending
          .Select(id => current[id]!.Value)
          .Distinct()
          .ToList();

        var parents = await db.Markers
          .Where(m => nodeIds.Contains(m.id))
          .Select(m => new { m.id, m.parent_id })
          .ToDictionaryAsync(m => m.id, m => m.parent_id);

        var candidates = new HashSet<Guid>();
        foreach (var start in pending.ToList())
        {
          var node = current[start]!.Value;
          if (!parents.TryGetValue(node, out var parent) ||
              parent is null ||
              !visited[start].Add(parent.Value))
          {
            // The root, or a cycle: nothing above this node is a canvas.
            current[start] = null;
            pending.Remove(start);
            continue;
          }

          current[start] = parent;
          candidates.Add(parent.Value);
        }

        if (candidates.Count == 0)
          continue;

        var canvases = new HashSet<Guid>(
          await db.Spaces.Where(s => candidates.Contains(s.object_id))
            .Select(s => s.object_id).ToListAsync());
        canvases.UnionWith(
          await db.Diagrams.Where(d => candidates.Contains(d.id))
            .Select(d => d.id).ToListAsync());

        foreach (var start in pending.ToList())
        {
          var node = current[start];
          if (node.HasValue && canvases.Contains(node.Value))
          {
            result[start] = node;
            pending.Remove(start);
          }
        }
      }

      return result;
    }
  }
}
