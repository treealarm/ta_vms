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
  // The one place that changes a camera's state set. Two producers write it — the alarm adapter
  // and the connectivity poller — and a read-modify-write from both could lose one of them, so the
  // change happens under an advisory lock inside a transaction. The full set is written, as the
  // states table expects; states owned by somebody else are preserved.
  internal sealed class CameraStateWriter
  {
    public const string Alarm = "ALARM";
    public const string Norm = "NORM";
    public const string Connected = "CONNECTED";
    public const string Disconnected = "DISCONNECTED";

    private const string LockSql = "SELECT pg_advisory_xact_lock(hashtext('ta_map_camera_state'));";

    private readonly VmsMapDbContext _db;
    private readonly IStateService _stateService;
    private readonly IStatesUpdateService _statesUpdateService;

    public CameraStateWriter(
      VmsMapDbContext db,
      IStateService stateService,
      IStatesUpdateService statesUpdateService)
    {
      _db = db ?? throw new ArgumentNullException(nameof(db));
      _stateService = stateService ?? throw new ArgumentNullException(nameof(stateService));
      _statesUpdateService = statesUpdateService
        ?? throw new ArgumentNullException(nameof(statesUpdateService));
    }

    public Task SetAlarmAsync(string cameraId, CancellationToken ct) =>
      ChangeAsync(cameraId, add: [Alarm], remove: [Norm], ct);

    public Task ClearAlarmAsync(string cameraId, CancellationToken ct) =>
      ChangeAsync(cameraId, add: [Norm], remove: [Alarm], ct);

    public Task SetConnectedAsync(string cameraId, bool connected, CancellationToken ct) =>
      ChangeAsync(
        cameraId,
        add: [connected ? Connected : Disconnected],
        remove: [connected ? Disconnected : Connected],
        ct);

    private async Task ChangeAsync(
      string cameraId,
      IReadOnlyCollection<string> add,
      IReadOnlyCollection<string> remove,
      CancellationToken ct)
    {
      var canonicalId = CameraObjectId.For(cameraId);

      // Join an outer transaction when the alarm writer is putting the state and the event in
      // one; otherwise own one.
      var ownsTransaction = _db.Database.IsRelational()
        && _db.Database.CurrentTransaction is null;
      await using var tx = ownsTransaction
        ? await _db.Database.BeginTransactionAsync(ct)
        : null;

      if (_db.Database.IsRelational())
        await _db.Database.ExecuteSqlRawAsync(LockSql, ct);

      var current = (await _stateService.GetStatesAsync([canonicalId]))
        .GetValueOrDefault(canonicalId)?.states?.ToHashSet(StringComparer.Ordinal)
        ?? [];

      var next = new HashSet<string>(current, StringComparer.Ordinal);
      next.ExceptWith(remove);
      next.UnionWith(add);

      if (next.SetEquals(current))
      {
        if (tx is not null)
          await tx.CommitAsync(ct);
        return;
      }

      await _statesUpdateService.UpdateStates([
        new ObjectStateDTO
        {
          id = canonicalId,
          states = next.OrderBy(s => s, StringComparer.Ordinal).ToList(),
          timestamp = DateTime.UtcNow,
        }
      ]);

      if (tx is not null)
        await tx.CommitAsync(ct);
    }
  }
}
