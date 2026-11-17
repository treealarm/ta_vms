using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using VmsMap.Domain;

namespace VmsMap.Db
{
  /// <summary>
  /// What may stand under what. The specialized writers -- <c>UpsertSpaceAsync</c>, the placement
  /// writer, the device adapter -- each check their own model, but the general "move to" RPC reaches
  /// the same tree with no model at all. The rules live here once and are checked from
  /// <see cref="MapDb.UpdateHierarchyAsync"/>, so a node moved from any entry point obeys them.
  /// </summary>
  /// <remarks>
  /// Only a move of an existing typed node is checked: a node's kind is its side row, and a node
  /// created in the same call does not have one yet. Creation goes through the specialized writers,
  /// which enforce their own shape. Kinds with no rule (a container, a picture, a landmark, a
  /// diagram, a device part) are left unconstrained on purpose: their parent is a canvas
  /// arrangement, not an invariant of the model.
  /// </remarks>
  internal static class TreeRules
  {
    public static async Task ValidateMovesAsync(
      VmsMapDbContext db,
      IReadOnlyDictionary<Guid, Guid?> moves,
      IReadOnlyDictionary<Guid, DBMarker> existing,
      CancellationToken ct = default)
    {
      if (moves.Count == 0)
        return;

      var childIds = moves.Keys.ToList();
      var parentIds = moves.Values
        .Where(parent => parent.HasValue)
        .Select(parent => parent!.Value)
        .Distinct()
        .ToList();

      var childKinds = await ResolveKindsAsync(db, childIds, ct);
      var parentKinds = await ResolveKindsAsync(db, parentIds, ct);

      var spaceKinds = await db.Spaces.AsNoTracking()
        .Where(space => childIds.Contains(space.object_id))
        .ToDictionaryAsync(space => space.object_id, space => space.kind, ct);

      foreach (var (child, parent) in moves)
      {
        switch (childKinds[child])
        {
          case NodeKind.Space:
            // A map is the real world and always a root; a plan is the thing that nests.
            if (spaceKinds.GetValueOrDefault(child) == SpaceKinds.Map)
            {
              if (parent is not null)
                throw new ArgumentException(
                    $"A map is always a root; node '{Utils.ConvertGuidToObjectId(child)}' cannot be "
                    + "moved under another node");
            }
            else if (parent is not null && parentKinds.GetValueOrDefault(parent.Value) != NodeKind.Space)
            {
              throw new ArgumentException(
                  $"A plan must stand inside a space; node '{Utils.ConvertGuidToObjectId(child)}' "
                  + "cannot be moved under a non-space node");
            }
            break;

          case NodeKind.Camera:
            // The camera's own node stands under its server; what is drawn on a canvas is a
            // replica of it, and a replica is free to hang under any container there.
            if (existing.TryGetValue(child, out var row) && row.owner_id is null
                && parent is not null
                && parentKinds.GetValueOrDefault(parent.Value) != NodeKind.Object)
            {
              throw new ArgumentException(
                  $"A camera's own node stands under its server, not under a map object; node "
                  + $"'{Utils.ConvertGuidToObjectId(child)}' cannot be moved there");
            }
            break;
        }
      }
    }

    private static async Task<Dictionary<Guid, NodeKind>> ResolveKindsAsync(
      VmsMapDbContext db, IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
      if (ids.Count == 0)
        return new Dictionary<Guid, NodeKind>();

      var resolved = await NodeKindLookup.ResolveAsync(
        db, ids.Select(id => Utils.ConvertGuidToObjectId(id)!), ct);
      return resolved.ToDictionary(
        pair => Utils.ConvertObjectIdToGuid(pair.Key)!.Value,
        pair => pair.Value);
    }
  }
}
