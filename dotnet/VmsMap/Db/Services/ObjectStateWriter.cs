using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using VmsMap.Domain;

namespace VmsMap.Db
{
  // The one place that changes any object's state set. Two producers can write the same node from
  // different paths — the alarm adapter, the connectivity poller, an a device event — and a
  // read-modify-write from both could lose one of them, so the change happens under the map's
  // camera-state lock inside a transaction. The full set is written, as the states table expects;
  // states owned by somebody else are preserved by the caller passing the set it means.
  //
  // CameraStateWriter's helpers are thin wrappers over this; AlarmStore (M2.4c) writes through it
  // too, which is why the lock order lives in MapLocks and not here.
  public sealed class ObjectStateWriter
  {
    private readonly VmsMapDbContext _db;
    private readonly IStateService _stateService;
    private readonly IStatesUpdateService _statesUpdateService;

    public ObjectStateWriter(
      VmsMapDbContext db,
      IStateService stateService,
      IStatesUpdateService statesUpdateService)
    {
      _db = db ?? throw new ArgumentNullException(nameof(db));
      _stateService = stateService ?? throw new ArgumentNullException(nameof(stateService));
      _statesUpdateService = statesUpdateService
        ?? throw new ArgumentNullException(nameof(statesUpdateService));
    }

    /// <summary>
    /// Writes the full state set of one object and answers whether it changed. The alarm
    /// aggregation that follows runs inside the same transaction — a state written without its
    /// counters would leave the tree wrong until the next update.
    /// </summary>
    public Task<bool> SetStatesAsync(
      string objectId, IReadOnlyCollection<string> states, CancellationToken ct) =>
      MutateAsync(objectId, _ => new HashSet<string>(states, StringComparer.Ordinal), ct);

    /// <summary>Adds and removes states, keeping the rest of the set as it was.</summary>
    public Task<bool> ChangeAsync(
      string objectId,
      IReadOnlyCollection<string> add,
      IReadOnlyCollection<string> remove,
      CancellationToken ct) =>
      MutateAsync(objectId, current =>
      {
        var next = new HashSet<string>(current, StringComparer.Ordinal);
        next.ExceptWith(remove);
        next.UnionWith(add);
        return next;
      }, ct);

    private async Task<bool> MutateAsync(
      string objectId, Func<HashSet<string>, HashSet<string>> nextSet, CancellationToken ct)
    {
      // Join an outer transaction when the caller is putting the state, its event and the outbox
      // row in one; otherwise own one.
      var ownsTransaction = _db.Database.IsRelational()
        && _db.Database.CurrentTransaction is null;
      await using var tx = ownsTransaction
        ? await _db.Database.BeginTransactionAsync(ct)
        : null;

      await MapLocks.AcquireCameraStateAsync(_db, ct);

      var current = await CurrentAsync(objectId, ct);
      var next = nextSet(current);
      if (next.SetEquals(current))
      {
        if (tx is not null)
          await tx.CommitAsync(ct);
        return false;
      }

      await _statesUpdateService.UpdateStates([
        new ObjectStateDTO
        {
          id = objectId,
          states = next.OrderBy(s => s, StringComparer.Ordinal).ToList(),
          timestamp = DateTime.UtcNow,
        }
      ]);

      if (tx is not null)
        await tx.CommitAsync(ct);
      return true;
    }

    private async Task<HashSet<string>> CurrentAsync(string objectId, CancellationToken ct)
    {
      var states = (await _stateService.GetStatesAsync([objectId]))
        .GetValueOrDefault(objectId)?.states;
      return states is null
        ? new HashSet<string>(StringComparer.Ordinal)
        : new HashSet<string>(states, StringComparer.Ordinal);
    }
  }
}
