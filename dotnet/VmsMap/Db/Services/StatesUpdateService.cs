using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using VmsMap.Domain;

namespace VmsMap.Db
{
  // Object states and the alarm aggregation that follows them. One transaction per update, and a
  // single advisory lock for the recomputation: alarm updates are rare, and serialising them is
  // cheaper than locking a subtree per update.
  internal class StatesUpdateService : IStatesUpdateService
  {
    private readonly IStateService _stateService;
    private readonly IMapService _mapService;
    private readonly VmsMapDbContext _db;

    public StatesUpdateService(
      IStateService stateService,
      IMapService mapService,
      VmsMapDbContext db)
    {
      _stateService = stateService;
      _mapService = mapService;
      _db = db;
    }

    public async Task<bool> UpdateStates(List<ObjectStateDTO> objStates)
    {
      if (objStates.Count == 0)
      {
        return true;
      }

      // Replicas report their owner's state; only the owner keeps a row of its own.
      var ids = objStates.Select(s => s.id ?? string.Empty).ToList();
      var owners = await _mapService.GetOwnersAsync(ids);
      objStates.RemoveAll(s => !owners.ContainsKey(s.id ?? string.Empty));

      if (objStates.Count == 0)
      {
        return true;
      }

      var changed = objStates
        .Select(s => Utils.ConvertObjectIdToGuid(s.id))
        .Where(g => g.HasValue)
        .Select(g => g!.Value)
        .ToList();

      // An adapter may already have opened a transaction to write a state and an event together;
      // in that case this method joins it and the caller commits. Otherwise it owns one.
      var ownsTransaction = _db.Database.IsRelational()
        && _db.Database.CurrentTransaction is null;
      await using var tx = ownsTransaction
        ? await _db.Database.BeginTransactionAsync()
        : null;
      await LockAggregationAsync();

      await _stateService.UpdateStatesAsync(objStates);
      await RecomputeAsync(changed);

      if (tx is not null)
        await tx.CommitAsync();
      return true;
    }

    public async Task<long> UpdateStateDescrs(List<ObjectStateDescriptionDTO> newObjs)
    {
      var updated = await _stateService.UpdateStateDescrsAsync(newObjs);

      // The recomputation is SQL over the alarm tables; the in-memory provider the tests use has
      // no SQL, and the pure aggregation is covered by AlarmAggregationTests.
      if (!_db.Database.IsRelational())
        return updated;

      // The transaction comes first: it is what opens the context's connection, and the recompute
      // reads and writes through raw commands on that same connection.
      await using var tx = await _db.Database.BeginTransactionAsync();

      // A changed alarm flag changes which objects are alarmed, not only how they are displayed.
      var withStates = await QueryIdsAsync(
        "SELECT DISTINCT object_id FROM object_state_values;", null);

      await LockAggregationAsync();
      await RecomputeAsync(withStates);
      await tx.CommitAsync();

      return updated;
    }

    public async Task RefreshAlarmsAsync(IReadOnlyCollection<string> objectIds)
    {
      if (!_db.Database.IsRelational())
        return;

      var ids = objectIds
        .Select(Utils.ConvertObjectIdToGuid)
        .Where(g => g.HasValue)
        .Select(g => g!.Value)
        .ToList();

      if (ids.Count == 0)
      {
        return;
      }

      if (_db.Database.CurrentTransaction == null)
      {
        await using var tx = await _db.Database.BeginTransactionAsync();
        await LockAggregationAsync();
        await RecomputeAsync(ids);
        await tx.CommitAsync();
      }
      else
      {
        await LockAggregationAsync();
        await RecomputeAsync(ids);
      }
    }

    private async Task LockAggregationAsync()
    {
      if (!_db.Database.IsRelational())
        return;

      await _db.Database.ExecuteSqlRawAsync(
        "SELECT pg_advisory_xact_lock(hashtext('ta_map_alarm'));");
    }

    // Recomputes the alarm rows of `changed` and every ancestor: the path to the root is small,
    // and a full recomputation of it cannot drift the way an incrementing counter can.
    private async Task RecomputeAsync(IReadOnlyCollection<Guid> changed)
    {
      if (changed.Count == 0 || !_db.Database.IsRelational())
      {
        return;
      }

      var closure = await LoadAncestorsAsync(changed);
      if (closure.Count == 0)
      {
        return;
      }

      var closureIds = closure.Select(c => c.Id).ToHashSet();

      // Children of the closure, so a parent can count them; they are not recomputed themselves.
      var children = await QueryLinksAsync(
        "SELECT id, parent_id FROM objects WHERE parent_id = ANY(@ids);", closureIds.ToList());

      var involved = closure
        .Concat(children)
        .GroupBy(n => n.Id)
        .Select(g => g.First())
        .ToList();

      var involvedIds = involved.Select(n => n.Id).ToList();

      var alarmNames = await QueryStringsAsync(
        "SELECT state FROM object_state_descriptions WHERE alarm;");

      var byId = involved.ToDictionary(n => n.Id);

      var ownAlarm = alarmNames.Count == 0
        ? new HashSet<Guid>()
        : await QueryIdsAsync(
            "SELECT DISTINCT object_id FROM object_state_values WHERE object_id = ANY(@ids) AND state = ANY(@states);",
            involvedIds,
            alarmNames);

      var stored = await QueryAlarmRowsAsync(involvedIds);

      var nodes = involved.Select(n =>
      {
        stored.TryGetValue(n.Id, out var row);
        return new AlarmNode(n.Id, n.ParentId, ownAlarm.Contains(n.Id), row.Alarm, row.Children);
      }).ToList();

      var updates = AlarmAggregation.Compute(nodes, closureIds);
      if (updates.Count == 0)
      {
        return;
      }

      var existing = await _db.AlarmStates
        .Where(a => involvedIds.Contains(a.id))
        .ToDictionaryAsync(a => a.id);

      foreach (var (id, value) in updates)
      {
        if (existing.TryGetValue(id, out var row))
        {
          row.alarm = value.Alarm;
          row.children_alarms = value.Children;
        }
        else
        {
          _db.AlarmStates.Add(new DBAlarmState
          {
            id = id,
            alarm = value.Alarm,
            children_alarms = value.Children
          });
        }
      }

      await _db.SaveChangesAsync();
    }

    private async Task<List<(Guid Id, Guid? ParentId)>> LoadAncestorsAsync(
      IReadOnlyCollection<Guid> ids)
    {
      return await QueryLinksAsync("""
        WITH RECURSIVE up AS (
          SELECT id, parent_id FROM objects WHERE id = ANY(@ids)
          UNION
          SELECT o.id, o.parent_id FROM objects o JOIN up ON o.id = up.parent_id
        )
        SELECT id, parent_id FROM up;
        """, ids.ToList());
    }

    private async Task<List<(Guid Id, Guid? ParentId)>> QueryLinksAsync(
      string sql, List<Guid> ids)
    {
      var result = new List<(Guid, Guid?)>();

      await using var cmd = CreateCommand(sql);
      cmd.Parameters.Add(new NpgsqlParameter("ids", ids.ToArray()));

      await using var reader = await cmd.ExecuteReaderAsync();
      while (await reader.ReadAsync())
      {
        result.Add((
          reader.GetGuid(0),
          reader.IsDBNull(1) ? null : reader.GetGuid(1)));
      }

      return result;
    }

    private async Task<HashSet<Guid>> QueryIdsAsync(
      string sql, List<Guid>? ids, List<string>? states = null)
    {
      var result = new HashSet<Guid>();

      await using var cmd = CreateCommand(sql);
      if (ids != null)
      {
        cmd.Parameters.Add(new NpgsqlParameter("ids", ids.ToArray()));
      }
      if (states != null)
      {
        cmd.Parameters.Add(new NpgsqlParameter("states", states.ToArray()));
      }

      await using var reader = await cmd.ExecuteReaderAsync();
      while (await reader.ReadAsync())
      {
        result.Add(reader.GetGuid(0));
      }

      return result;
    }

    private async Task<List<string>> QueryStringsAsync(string sql)
    {
      var result = new List<string>();

      await using var cmd = CreateCommand(sql);
      await using var reader = await cmd.ExecuteReaderAsync();
      while (await reader.ReadAsync())
      {
        result.Add(reader.GetString(0));
      }

      return result;
    }

    private async Task<Dictionary<Guid, (bool Alarm, int Children)>> QueryAlarmRowsAsync(
      List<Guid> ids)
    {
      var result = new Dictionary<Guid, (bool, int)>();

      await using var cmd = CreateCommand(
        "SELECT id, alarm, children_alarms FROM alarm_states WHERE id = ANY(@ids);");
      cmd.Parameters.Add(new NpgsqlParameter("ids", ids.ToArray()));

      await using var reader = await cmd.ExecuteReaderAsync();
      while (await reader.ReadAsync())
      {
        result[reader.GetGuid(0)] = (reader.GetBoolean(1), reader.GetInt32(2));
      }

      return result;
    }

    private DbCommand CreateCommand(string sql)
    {
      var cmd = _db.Database.GetDbConnection().CreateCommand();
      cmd.CommandText = sql;
      cmd.Transaction = _db.Database.CurrentTransaction?.GetDbTransaction();
      return cmd;
    }
  }
}
