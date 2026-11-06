using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using VmsMap.Clients;
using VmsMap.Db;
using VmsMap.Domain;
using VmsMap.Domain.Arming;

namespace VmsMap.Adapter
{
  /// <summary>Everything a raise needs to know about the condition being reported.</summary>
  public sealed record RaiseAlarmSpec(
    Guid ObjectId,
    string Source,
    string DedupKey,
    Guid? DefinitionId = null,
    Guid? SubscriptionId = null,
    string? Severity = null,
    DateTime? ExpectedEndTs = null,
    Guid? SourceEventId = null,
    string? Trigger = null);

  public enum AlarmMove
  {
    /// <summary>The transition happened and was written.</summary>
    Applied,
    /// <summary>The alarm was already in the state the caller asked for; nothing changed.</summary>
    Unchanged,
    /// <summary>The transition is forbidden from the row's current state.</summary>
    Refused,
    NotFound,
  }

  public sealed record AlarmOutcome(AlarmMove Move, DBAlarm? Alarm, string? Reason = null);

  /// <summary>
  /// The operator's alarm entity: raise, return to normal, acknowledge and close, each one
  /// transaction that takes the map's locks first, locks the alarm row, then writes the object's
  /// state and the lifecycle event.
  /// </summary>
  /// <remarks>
  /// The row is the single truth for what may happen next; the table in
  /// <c>docs/plans/square-merge/m2.4c-alarms.md</c> is its transition table. Two rules shape the
  /// code: an acknowledgement never clears <c>ALARM</c> — the physical condition is still there —
  /// and <c>ALARM</c> comes off only when no open alarm on the object still has an active
  /// condition.
  /// <para>
  /// Acknowledging is gated by the effective arming, but that check is a UX gate, not a lock: the
  /// arming lives in two other services and cannot be serialised with this transaction. Disarming
  /// concurrently may refuse one acknowledgement — the operator repeats it — and the other order
  /// (the ack got in first) does nothing an unarmed node would not allow anyway.
  /// </para>
  /// </remarks>
  public sealed class AlarmStore
  {
    public const string RaisedEvent = "alarm_raised";
    public const string AcknowledgedEvent = "alarm_acknowledged";
    public const string ReturnedEvent = "alarm_returned";
    public const string ClosedEvent = "alarm_closed";

    private const string SystemActor = "system";

    private readonly VmsMapDbContext _db;
    private readonly ObjectStateWriter _states;
    private readonly EventsWriter _events;
    private readonly IAlarmDefinitionsSource _definitions;
    private readonly EffectiveArmingResolver _arming;
    private readonly ILogger<AlarmStore> _logger;

    public AlarmStore(
      VmsMapDbContext db,
      ObjectStateWriter states,
      EventsWriter events,
      IAlarmDefinitionsSource definitions,
      EffectiveArmingResolver arming,
      ILogger<AlarmStore> logger)
    {
      _db = db ?? throw new ArgumentNullException(nameof(db));
      _states = states ?? throw new ArgumentNullException(nameof(states));
      _events = events ?? throw new ArgumentNullException(nameof(events));
      _definitions = definitions ?? throw new ArgumentNullException(nameof(definitions));
      _arming = arming ?? throw new ArgumentNullException(nameof(arming));
      _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    // ── Reads ────────────────────────────────────────────────────────────────────────────────────

    public async Task<DBAlarm?> FindAsync(Guid id, CancellationToken ct) =>
      await _db.Alarms.AsNoTracking().FirstOrDefaultAsync(a => a.id == id, ct);

    /// <summary>
    /// The open alarms raised after <paramref name="after"/>, newest first. Keyset, not offset: the
    /// open branch is ordered by (raised_at, id) and served by ix_alarms_open.
    /// </summary>
    public Task<List<DBAlarm>> OpenPageAsync(
      DateTime? after, Guid? afterId, int limit, CancellationToken ct)
    {
      var query = _db.Alarms.AsNoTracking()
        .Where(a => a.status != AlarmStatus.Closed);

      if (after is not null && afterId is not null)
      {
        query = query.Where(a =>
          a.raised_at < after.Value
          || (a.raised_at == after.Value && a.id.CompareTo(afterId.Value) < 0));
      }

      return query
        .OrderByDescending(a => a.raised_at)
        .ThenByDescending(a => a.id)
        .Take(limit)
        .ToListAsync(ct);
    }

    /// <summary>
    /// The alarms closed since <paramref name="since"/>, newest close first, by (closed_at, id) and
    /// ix_alarms_closed_at. A separate cursor from the open branch: one index does not serve both.
    /// </summary>
    public Task<List<DBAlarm>> ClosedPageAsync(
      DateTime since, DateTime? after, Guid? afterId, int limit, CancellationToken ct)
    {
      var query = _db.Alarms.AsNoTracking()
        .Where(a => a.status == AlarmStatus.Closed
                    && a.closed_at != null
                    && a.closed_at >= since);

      if (after is not null && afterId is not null)
      {
        query = query.Where(a =>
          a.closed_at < after.Value
          || (a.closed_at == after.Value && a.id.CompareTo(afterId.Value) < 0));
      }

      return query
        .OrderByDescending(a => a.closed_at)
        .ThenByDescending(a => a.id)
        .Take(limit)
        .ToListAsync(ct);
    }

    /// <summary>The effective arming of the node an alarm stands on, for the ack gate.</summary>
    public async Task<bool> IsArmedAsync(Guid objectId, CancellationToken ct)
    {
      var arming = await _arming.ResolveAsync(Utils.ConvertGuidToObjectId(objectId), ct);
      return arming?.Armed ?? false;
    }

    // ── Transitions ──────────────────────────────────────────────────────────────────────────────

    public async Task<AlarmOutcome> RaiseAsync(RaiseAlarmSpec spec, CancellationToken ct)
    {
      // Resolved before the transaction opens: the cache reads VmsCfg over the network when it is
      // stale, and a network call must not happen while the map's advisory locks are held.
      var definition = await _definitions.FindAsync(spec.Source, ct);

      await using var tx = await BeginAsync(ct);

      var existing = await LockOpenAsync(spec.ObjectId, spec.DedupKey, ct);
      if (existing is not null)
      {
        if (existing.status != AlarmStatus.Returned)
        {
          // Still active or acknowledged: the condition never went away. A retention renewal may
          // move the projected end; nothing else about the alarm changes and no second event is
          // written.
          if (spec.ExpectedEndTs is not null && spec.ExpectedEndTs != existing.expected_end_ts)
          {
            existing.expected_end_ts = spec.ExpectedEndTs;
            await _db.SaveChangesAsync(ct);
          }

          await CommitAsync(tx, ct);
          return new AlarmOutcome(AlarmMove.Unchanged, existing);
        }

        // It came back. The row keeps its id and its history; a returned alarm that was never
        // closed becomes active again.
        existing.status = AlarmStatus.Active;
        existing.condition_active = true;
        existing.cleared_at = null;
        if (spec.ExpectedEndTs is not null)
          existing.expected_end_ts = spec.ExpectedEndTs;

        await _db.SaveChangesAsync(ct);
        await SetAlarmStateAsync(existing.object_id, ct);
        if (spec.Source != AlarmSource.Retention)
          await WriteEventAsync(existing, RaisedEvent, existing.severity, spec.Trigger, ct,
            ("source_event_id", spec.SourceEventId?.ToString()));

        await CommitAsync(tx, ct);
        return new AlarmOutcome(AlarmMove.Applied, existing);
      }

      if (definition is null)
      {
        _logger.LogWarning(
          "Raising a {Source} alarm for {ObjectId} without a definition; using severity '{Severity}'",
          spec.Source, spec.ObjectId, AlarmSeverity.Warning);
      }

      var alarm = new DBAlarm
      {
        id = Guid.NewGuid(),
        object_id = spec.ObjectId,
        definition_id = definition is null ? spec.DefinitionId : Guid.Parse(definition.Id),
        subscription_id = spec.SubscriptionId,
        source = spec.Source,
        dedup_key = spec.DedupKey,
        condition_active = true,
        status = AlarmStatus.Active,
        severity = spec.Severity
          ?? definition?.Severity
          ?? AlarmSeverity.Warning,
        raised_at = DateTime.UtcNow,
        expected_end_ts = spec.ExpectedEndTs,
        source_event_id = spec.SourceEventId,
      };

      _db.Alarms.Add(alarm);
      await _db.SaveChangesAsync(ct);

      await SetAlarmStateAsync(alarm.object_id, ct);

      // Retention keeps its legacy `alarm` event, which the journal has always shown; a second
      // alarm_raised next to it would be the same fact twice.
      if (spec.Source != AlarmSource.Retention)
        await WriteEventAsync(alarm, RaisedEvent, alarm.severity, spec.Trigger, ct,
          ("source_event_id", spec.SourceEventId?.ToString()));

      await CommitAsync(tx, ct);
      return new AlarmOutcome(AlarmMove.Applied, alarm);
    }

    /// <summary>
    /// The condition went away: an active alarm becomes <c>returned</c> (waiting for an operator)
    /// or, under auto_on_normal, <c>closed</c>; an acknowledged one closes.
    /// </summary>
    public async Task<AlarmOutcome> NormalAsync(
      Guid objectId, string dedupKey, CancellationToken ct)
    {
      // The whole (tiny) list is cached; reading it now keeps the network out of the transaction.
      var definitions = await _definitions.ListAsync(ct);

      await using var tx = await BeginAsync(ct);

      var alarm = await LockOpenAsync(objectId, dedupKey, ct);
      if (alarm is null || !alarm.condition_active)
      {
        await CommitAsync(tx, ct);
        return alarm is null
          ? new AlarmOutcome(AlarmMove.Unchanged, null)
          : new AlarmOutcome(AlarmMove.Unchanged, alarm);
      }

      var now = DateTime.UtcNow;

      switch (alarm.status)
      {
        case AlarmStatus.Active:
          alarm.condition_active = false;
          alarm.cleared_at = now;

          var definition = alarm.definition_id is { } id
            ? definitions.FirstOrDefault(d =>
                string.Equals(d.Id, id.ToString(), StringComparison.Ordinal))
            : null;

          if (definition?.AckPolicy == AlarmAckPolicy.AutoOnNormal)
          {
            alarm.status = AlarmStatus.Closed;
            alarm.closed_at = now;
            alarm.close_reason = AlarmCloseReason.AutoReturnedToNormal;
            alarm.closed_by = SystemActor;
            await _db.SaveChangesAsync(ct);
            await WriteEventAsync(alarm, ClosedEvent, null, null, ct,
              ("close_reason", alarm.close_reason),
              ("close_comment", null));
          }
          else
          {
            alarm.status = AlarmStatus.Returned;
            await _db.SaveChangesAsync(ct);
            await WriteEventAsync(alarm, ReturnedEvent, null, null, ct);
          }
          break;

        case AlarmStatus.Acknowledged:
          // The operator dealt with it and the condition has now cleared as well.
          alarm.status = AlarmStatus.Closed;
          alarm.condition_active = false;
          alarm.cleared_at ??= now;
          alarm.closed_at = now;
          alarm.close_reason = AlarmCloseReason.ReturnedToNormal;
          alarm.closed_by = alarm.acknowledged_by ?? SystemActor;
          await _db.SaveChangesAsync(ct);
          await WriteEventAsync(alarm, ClosedEvent, null, null, ct,
            ("close_reason", alarm.close_reason),
            ("close_comment", null));
          break;

        default:
          await CommitAsync(tx, ct);
          return new AlarmOutcome(AlarmMove.Unchanged, alarm);
      }

      await SyncObjectAlarmStateAsync(alarm.object_id, ct);

      await CommitAsync(tx, ct);
      return new AlarmOutcome(AlarmMove.Applied, alarm);
    }

    /// <summary>A renewal of a retention episode moves the projected end without touching anything else.</summary>
    public async Task SetExpectedEndAsync(
      Guid objectId, string dedupKey, DateTime? end, CancellationToken ct)
    {
      if (end is null)
        return;

      await using var tx = await BeginAsync(ct);

      var alarm = await LockOpenAsync(objectId, dedupKey, ct);
      if (alarm is not null && alarm.expected_end_ts != end)
      {
        alarm.expected_end_ts = end;
        await _db.SaveChangesAsync(ct);
      }

      await CommitAsync(tx, ct);
    }

    public async Task<AlarmOutcome> AcknowledgeAsync(
      Guid alarmId, string actor, string? comment, CancellationToken ct)
    {
      // The arming is resolved before the row is locked: it comes from three other services, and
      // the plan calls this gate a UX check rather than a lock. The row is re-checked below, so a
      // condition that cleared in between simply takes the path it now deserves.
      var known = await _db.Alarms.AsNoTracking().FirstOrDefaultAsync(a => a.id == alarmId, ct);
      var armed = known is { status: AlarmStatus.Active, condition_active: true }
        && await IsArmedAsync(known.object_id, ct);

      await using var tx = await BeginAsync(ct);

      var alarm = await LockAsync(alarmId, ct);
      if (alarm is null)
      {
        await CommitAsync(tx, ct);
        return new AlarmOutcome(AlarmMove.NotFound, null);
      }

      if (alarm.status == AlarmStatus.Closed)
      {
        await CommitAsync(tx, ct);
        return new AlarmOutcome(AlarmMove.Refused, alarm, "the alarm is closed");
      }

      if (alarm.status == AlarmStatus.Acknowledged)
      {
        await CommitAsync(tx, ct);
        return new AlarmOutcome(AlarmMove.Unchanged, alarm);
      }

      var now = DateTime.UtcNow;

      if (alarm.status == AlarmStatus.Active)
      {
        // A live condition may only be acknowledged on a node the operator has disarmed: an
        // acknowledgement is "I have seen it", not "make it go away".
        if (alarm.condition_active && armed)
        {
          await CommitAsync(tx, ct);
          return new AlarmOutcome(
            AlarmMove.Refused, alarm, "the condition is active and the node is armed");
        }

        alarm.status = AlarmStatus.Acknowledged;
        alarm.acknowledged_at = now;
        alarm.acknowledged_by = actor;
        alarm.ack_comment = comment;
        await _db.SaveChangesAsync(ct);

        await WriteEventAsync(alarm, AcknowledgedEvent, null, null, ct,
          ("acknowledged_by", actor),
          ("comment", comment));

        await CommitAsync(tx, ct);
        return new AlarmOutcome(AlarmMove.Applied, alarm);
      }

      // Returned: the condition is already gone, so the acknowledgement finishes the alarm.
      alarm.status = AlarmStatus.Closed;
      alarm.acknowledged_at = now;
      alarm.acknowledged_by = actor;
      alarm.ack_comment = comment;
      alarm.closed_at = now;
      alarm.close_reason = AlarmCloseReason.AcknowledgedAfterNormal;
      alarm.closed_by = actor;
      await _db.SaveChangesAsync(ct);

      await WriteEventAsync(alarm, AcknowledgedEvent, null, null, ct,
        ("acknowledged_by", actor),
        ("comment", comment));
      await WriteEventAsync(alarm, ClosedEvent, null, null, ct,
        ("close_reason", alarm.close_reason),
        ("close_comment", null));

      await CommitAsync(tx, ct);
      return new AlarmOutcome(AlarmMove.Applied, alarm);
    }

    /// <summary>
    /// An operator closing an alarm whose condition is already gone. An active condition may not be
    /// closed by hand — that would suppress an alarm that is still true; the reason says whether the
    /// alarm was real.
    /// </summary>
    public async Task<AlarmOutcome> CloseAsync(
      Guid alarmId, string actor, string reason, string? comment, CancellationToken ct)
    {
      await using var tx = await BeginAsync(ct);

      var alarm = await LockAsync(alarmId, ct);
      if (alarm is null)
      {
        await CommitAsync(tx, ct);
        return new AlarmOutcome(AlarmMove.NotFound, null);
      }

      if (alarm.status == AlarmStatus.Closed)
      {
        await CommitAsync(tx, ct);
        return new AlarmOutcome(AlarmMove.Unchanged, alarm);
      }

      if (alarm.condition_active)
      {
        await CommitAsync(tx, ct);
        return new AlarmOutcome(
          AlarmMove.Refused, alarm, "the condition is active; it cannot be closed by hand");
      }

      alarm.status = AlarmStatus.Closed;
      alarm.closed_at = DateTime.UtcNow;
      alarm.close_reason = reason;
      alarm.close_comment = comment;
      alarm.closed_by = actor;
      await _db.SaveChangesAsync(ct);

      await WriteEventAsync(alarm, ClosedEvent, null, null, ct,
        ("close_reason", alarm.close_reason),
        ("close_comment", comment));

      await CommitAsync(tx, ct);
      return new AlarmOutcome(AlarmMove.Applied, alarm);
    }

    /// <summary>
    /// The device or its part is gone: every open alarm on the node closes as device_gone. Being
    /// unreachable is not being gone — only a successful answer that no longer lists the part, or a
    /// removed camera, gets here.
    /// </summary>
    public async Task CloseForDeviceGoneAsync(Guid objectId, CancellationToken ct)
    {
      await using var tx = await BeginAsync(ct);

      var open = await _db.Alarms
        .Where(a => a.object_id == objectId && a.status != AlarmStatus.Closed)
        .ToListAsync(ct);
      if (open.Count == 0)
      {
        await CommitAsync(tx, ct);
        return;
      }

      var now = DateTime.UtcNow;
      foreach (var alarm in open)
      {
        alarm.status = AlarmStatus.Closed;
        alarm.condition_active = false;
        alarm.cleared_at ??= now;
        alarm.closed_at = now;
        alarm.close_reason = AlarmCloseReason.DeviceGone;
        alarm.closed_by = SystemActor;
      }

      await _db.SaveChangesAsync(ct);

      foreach (var alarm in open)
      {
        await WriteEventAsync(alarm, ClosedEvent, null, null, ct,
          ("close_reason", alarm.close_reason),
          ("close_comment", null));
      }

      await SyncObjectAlarmStateAsync(objectId, ct);
      await CommitAsync(tx, ct);
    }

    // ── Internals ────────────────────────────────────────────────────────────────────────────────

    // The map's locks are taken even when the caller already opened the transaction: an outer
    // transaction does not imply an outer lock. Everything below may take them again — an advisory
    // lock is re-entrant inside its transaction — but nothing takes them in reverse.
    private async Task<IDbContextTransaction?> BeginAsync(CancellationToken ct)
    {
      IDbContextTransaction? tx = null;
      if (_db.Database.IsRelational() && _db.Database.CurrentTransaction is null)
        tx = await _db.Database.BeginTransactionAsync(ct);

      await MapLocks.AcquireAllAsync(_db, ct);
      return tx;
    }

    private static Task CommitAsync(IDbContextTransaction? tx, CancellationToken ct) =>
      tx is null ? Task.CompletedTask : tx.CommitAsync(ct);

    private async Task<DBAlarm?> LockAsync(Guid id, CancellationToken ct)
    {
      if (!_db.Database.IsRelational())
        return await _db.Alarms.FirstOrDefaultAsync(a => a.id == id, ct);

      return await _db.Alarms
        .FromSqlInterpolated($"SELECT * FROM alarms WHERE id = {id} FOR UPDATE")
        .FirstOrDefaultAsync(ct);
    }

    private async Task<DBAlarm?> LockOpenAsync(Guid objectId, string dedupKey, CancellationToken ct)
    {
      if (!_db.Database.IsRelational())
        return await _db.Alarms.FirstOrDefaultAsync(
          a => a.object_id == objectId
               && a.dedup_key == dedupKey
               && a.status != AlarmStatus.Closed, ct);

      return await _db.Alarms
        .FromSqlInterpolated($"""
          SELECT * FROM alarms
          WHERE object_id = {objectId} AND dedup_key = {dedupKey} AND status <> 'closed'
          FOR UPDATE
          """)
        .FirstOrDefaultAsync(ct);
    }

    private async Task SetAlarmStateAsync(Guid objectId, CancellationToken ct) =>
      await _states.ChangeAsync(
        Utils.ConvertGuidToObjectId(objectId),
        add: [CameraStateWriter.Alarm],
        remove: [CameraStateWriter.Norm],
        ct);

    /// <summary>
    /// Takes <c>ALARM</c> off the node when nothing is alarming it any more. A camera goes back to
    /// NORM; a part keeps its sensor state and only loses ALARM.
    /// </summary>
    private async Task SyncObjectAlarmStateAsync(Guid objectId, CancellationToken ct)
    {
      await _db.SaveChangesAsync(ct);

      var stillAlarming = await _db.Alarms.AsNoTracking()
        .AnyAsync(a => a.object_id == objectId
                       && a.status != AlarmStatus.Closed
                       && a.condition_active, ct);
      if (stillAlarming)
        return;

      var isCamera = await _db.DeviceNodes.AsNoTracking()
        .AnyAsync(n => n.object_id == objectId
                       && n.part_kind == DeviceObjectId.DevicePart, ct);

      await _states.ChangeAsync(
        Utils.ConvertGuidToObjectId(objectId),
        add: isCamera ? [CameraStateWriter.Norm] : [],
        remove: [CameraStateWriter.Alarm],
        ct);
    }

    private async Task<string?> CameraIdAsync(Guid objectId, CancellationToken ct) =>
      await _db.DeviceNodes.AsNoTracking()
        .Where(n => n.object_id == objectId)
        .Select(n => n.source_id)
        .FirstOrDefaultAsync(ct);

    private async Task WriteEventAsync(
      DBAlarm alarm,
      string eventName,
      string? param0,
      string? param1,
      CancellationToken ct,
      params (string Name, string? Value)[] props)
    {
      var cameraId = await CameraIdAsync(alarm.object_id, ct);

      var extra = new List<ObjExtraPropertyDTO>
      {
        Text("alarm_id", alarm.id.ToString()),
      };
      if (cameraId is not null)
        extra.Add(Text("camera_id", cameraId));

      foreach (var (name, value) in props)
        extra.Add(Text(name, value));

      await _events.WriteAsync([
        new EventDTO
        {
          id = Guid.NewGuid().ToString(),
          object_id = Utils.ConvertGuidToObjectId(alarm.object_id),
          event_name = eventName,
          event_priority = 0,
          timestamp = DateTime.UtcNow,
          param0 = param0,
          param1 = param1,
          extra_props = extra,
        }
      ], ct);
    }

    private static ObjExtraPropertyDTO Text(string name, string? value) => new()
    {
      prop_name = name,
      str_val = value ?? string.Empty,
    };
  }
}
