using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace VmsMap.Domain
{
  // Rights on tree nodes, inherited downwards. Reads ask VisibleAsync whether a node may leave the
  // service at all; web_srv asks GetEffectiveAsync once per user and caches the answer.
  public interface IRightsService
  {
    // Which of the requested nodes the caller may see (`view`), through a row on the node or any
    // ancestor. In open mode a node with no row in its ancestry is visible to everyone.
    Task<HashSet<string>> VisibleAsync(
      IReadOnlyCollection<string> objectIds, CallerContext caller, CancellationToken ct);

    // Which of the requested nodes grant one action, in one pass. The batch form exists so a list
    // endpoint never asks per row: with 100 000 nodes that is the difference between one query per
    // level of the tree and one per row.
    Task<HashSet<string>> AllowedAsync(
      IReadOnlyCollection<string> objectIds, string action, CallerContext caller, CancellationToken ct);

    // Whether one action is granted on one node. Used by writes that check a right (CreateReplica).
    Task<bool> IsAllowedAsync(
      string objectId, string action, CallerContext caller, CancellationToken ct);

    // The explicit effective grants, expanded down the tree. In open mode a node absent from the
    // answer is open; in enforced mode it is denied. Admin and unrestricted callers do not need
    // the list at all and get an empty one.
    Task<List<NodeRightsDTO>> GetEffectiveAsync(CallerContext caller, CancellationToken ct);

    Task<RightsMode> GetModeAsync(CancellationToken ct);
    Task SetModeAsync(RightsMode mode, CancellationToken ct);
  }
}
