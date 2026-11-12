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

      await MapLocks.AcquireAlarmAsync(_db);
    }

    // Recomputes the alarm rows of `changed` and every ancestor: the path to the root is small,
    // and a full recomputation of it cannot drift the way an incrementing counter can.
    //
    // The cost is O(depth), not O(children). A parent's counter needs the number of its direct
    // children that are alarmed, but not the child rows themselves: the count is aggregated by
    // objects.parent_id inside the database, and only the closure's own rows are materialised. At
    // a server with 100 000 cameras the old version loaded all 100 000 on every alarm update.
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
      var closureList = closureIds.ToList();

      var alarmNames = await QueryStringsAsync(
        "SELECT state FROM object_state_descriptions WHERE alarm;");

      var ownAlarm = alarmNames.Count == 0
        ? new HashSet<Guid>()
        : await QueryIdsAsync(
            "SELECT DISTINCT object_id FROM object_state_values WHERE object_id = ANY(@ids) AND state = ANY(@states);",
            closureList,
            alarmNames);

      var stored = await QueryAlarmRowsAsync(closureList);

      // Children outside the closure: their stored rows already say whether they are alarmed, and
      // the aggregate answers with a number per closure node instead of one row per child.
      var outsideAlarmed = await QueryChildAlarmCountsAsync(closureList);

      var nodes = closure.Select(n =>
      {
        stored.TryGetValue(n.Id, out var row);
        return new AlarmNode(n.Id, n.ParentId, ownAlarm.Contains(n.Id), row.Alarm, row.Children);
      }).ToList();

      var updates = AlarmAggregation.Compute(nodes, closureIds, outsideAlarmed);
      if (updates.Count == 0)
      {
        return;
      }

      var existing = await _db.AlarmStates
        .Where(a => closureList.Contains(a.id))
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

      // The canonical tree ends at the server; the room a camera is drawn in hangs off its
      // reference replica instead. Mirroring the camera's row onto that one replica is what lets
      // the space count the alarm like any other child, without a row per picture.
      await MirrorToReferenceReplicasAsync(updates);
    }

    /// <summary>
    /// Copies the alarm row of every changed camera node onto its reference replica and recomputes
    /// the spaces above it. Only the reference replica gets a row: the others answer through their
    /// owner on read, and a row each would count the same camera again in every space it is drawn
    /// in.
    /// </summary>
    private async Task MirrorToReferenceReplicasAsync(
      IReadOnlyDictionary<Guid, (bool Alarm, int Children)> updates)
    {
      if (updates.Count == 0)
        return;

      var cameras = new Dictionary<Guid, Guid>(); // canonical node -> camera id
      await using (var cmd = CreateCommand("""
        SELECT dn.object_id, dn.source_id
        FROM device_node dn
        WHERE dn.object_id = ANY(@ids)
          AND dn.source_kind = 'camera'
          AND dn.part_kind = 'device';
        """))
      {
        cmd.Parameters.Add(new NpgsqlParameter("ids", updates.Keys.ToArray()));
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
          cameras[reader.GetGuid(0)] = reader.GetGuid(1);
      }

      if (cameras.Count == 0)
        return;

      var parents = await WriteMirrorsAsync(cameras);
      if (parents.Count > 0)
        await RecomputeAsync(parents);
    }

    /// <summary>
    /// Writes each camera's mirror onto its current reference replica and takes the row off every
    /// other picture of that camera, answering the parents whose counters changed.
    /// </summary>
    private async Task<HashSet<Guid>> WriteMirrorsAsync(
      IReadOnlyDictionary<Guid, Guid> cameras)
    {
      var replicas = new List<(Guid Id, Guid CameraId, bool IsReference, Guid? ParentId)>();
      await using (var cmd = CreateCommand("""
        SELECT p.object_id, p.camera_id, p.is_reference, o.parent_id
        FROM camera_placement p
        JOIN objects o ON o.id = p.object_id
        WHERE p.camera_id = ANY(@cams);
        """))
      {
        cmd.Parameters.Add(new NpgsqlParameter("cams", cameras.Values.Distinct().ToArray()));
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
          replicas.Add((
            reader.GetGuid(0),
            reader.GetGuid(1),
            reader.GetBoolean(2),
            reader.IsDBNull(3) ? null : reader.GetGuid(3)));
        }
      }

      if (replicas.Count == 0)
        return [];

      var states = await QueryAlarmRowsAsync(cameras.Keys.ToList());
      var byCamera = new Dictionary<Guid, (bool Alarm, int Children)>();
      foreach (var (node, cameraId) in cameras)
        byCamera[cameraId] = states.GetValueOrDefault(node);

      var parents = new HashSet<Guid>();
      var stale = new List<Guid>();

      foreach (var replica in replicas)
      {
        if (replica.ParentId is { } parent)
          parents.Add(parent);

        var state = byCamera.GetValueOrDefault(replica.CameraId);

        if (!replica.IsReference)
        {
          // A picture never keeps an alarm row of its own, whatever an earlier reference left on it.
          stale.Add(replica.Id);
          continue;
        }

        await using var cmd = CreateCommand("""
          INSERT INTO alarm_states (id, alarm, children_alarms) VALUES (@id, @alarm, @children)
          ON CONFLICT (id) DO UPDATE SET
            alarm = EXCLUDED.alarm,
            children_alarms = EXCLUDED.children_alarms
          WHERE alarm_states.alarm IS DISTINCT FROM EXCLUDED.alarm
             OR alarm_states.children_alarms IS DISTINCT FROM EXCLUDED.children_alarms;
          """);
        cmd.Parameters.Add(new NpgsqlParameter("id", replica.Id));
        cmd.Parameters.Add(new NpgsqlParameter("alarm", state.Alarm));
        cmd.Parameters.Add(new NpgsqlParameter("children", state.Children));
        await cmd.ExecuteNonQueryAsync();
      }

      if (stale.Count > 0)
      {
        await using var cmd = CreateCommand(
          "DELETE FROM alarm_states WHERE id = ANY(@ids);");
        cmd.Parameters.Add(new NpgsqlParameter("ids", stale.ToArray()));
        await cmd.ExecuteNonQueryAsync();
      }

      return parents;
    }

    /// <summary>
    /// Re-mirrors the cameras after a placement moved its reference: the old picture's row goes,
    /// the new one's appears, and both spaces recompute. Called by the placement write, where no
    /// state changed — only where the camera is drawn.
    /// </summary>
    public async Task RefreshPlacementMirrorsAsync(IReadOnlyCollection<string> cameraIds)
    {
      if (!_db.Database.IsRelational())
        return;

      var ids = cameraIds
        .Select(Utils.ConvertObjectIdToGuid)
        .Where(guid => guid.HasValue)
        .Select(guid => guid!.Value)
        .Distinct()
        .ToList();
      if (ids.Count == 0)
        return;

      var ownsTransaction = _db.Database.CurrentTransaction is null;
      await using var tx = ownsTransaction
        ? await _db.Database.BeginTransactionAsync()
        : null;
      await LockAggregationAsync();

      var cameras = new Dictionary<Guid, Guid>();
      await using (var cmd = CreateCommand("""
        SELECT dn.object_id, dn.source_id
        FROM device_node dn
        WHERE dn.source_id = ANY(@cams)
          AND dn.source_kind = 'camera'
          AND dn.part_kind = 'device';
        """))
      {
        cmd.Parameters.Add(new NpgsqlParameter("cams", ids.ToArray()));
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
          cameras[reader.GetGuid(0)] = reader.GetGuid(1);
      }

      if (cameras.Count > 0)
      {
        var parents = await WriteMirrorsAsync(cameras);
        if (parents.Count > 0)
          await RecomputeAsync(parents);
      }

      if (tx is not null)
        await tx.CommitAsync();
    }

    // One row per closure node, not one per child: the aggregate runs over idx_objects_parent_id
    // and the children of the closure never leave the database.
    private async Task<Dictionary<Guid, int>> QueryChildAlarmCountsAsync(List<Guid> closure)
    {
      var result = new Dictionary<Guid, int>();

      await using var cmd = CreateCommand("""
        SELECT o.parent_id, count(*) FILTER (WHERE a.alarm OR a.children_alarms > 0)
        FROM objects o
        LEFT JOIN alarm_states a ON a.id = o.id
        WHERE o.parent_id = ANY(@ids)
          AND NOT (o.id = ANY(@closure))
        GROUP BY o.parent_id;
        """);
      cmd.Parameters.Add(new NpgsqlParameter("ids", closure.ToArray()));
      cmd.Parameters.Add(new NpgsqlParameter("closure", closure.ToArray()));

      await using var reader = await cmd.ExecuteReaderAsync();
      while (await reader.ReadAsync())
      {
        result[reader.GetGuid(0)] = (int)reader.GetInt64(1);
      }

      return result;
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
