using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using VmsMap.Adapter;
using VmsMap.Db;
using VmsMap.Domain;
using MapProto = Vms.Map;

namespace VmsMap.Grpc
{
  // The alarm half of MapService: two keyset pages of rows, and the three transitions an outside
  // caller may ask for. The rows themselves are the store's, and the state rule that says which
  // transition is legal lives there too; this layer decides visibility and who may act.
  public partial class MapServiceImpl
  {
    private const int DefaultAlarmPageSize = 100;
    private const int MaxAlarmPageSize = 500;
    private const int DefaultClosedWindowMinutes = 15;

    public override async Task<MapProto.AlarmList> GetAlarms(
      MapProto.GetAlarmsRequest request, ServerCallContext context)
    {
      var ct = context.CancellationToken;
      var caller = ToCaller(request.Caller);
      var pageSize = request.PageSize <= 0
        ? DefaultAlarmPageSize
        : Math.Min(request.PageSize, MaxAlarmPageSize);
      var window = request.ClosedWindowMinutes <= 0
        ? DefaultClosedWindowMinutes
        : Math.Min(request.ClosedWindowMinutes, 24 * 60);

      var open = await PageAsync(
        request.OpenAfter,
        (after, afterId) => _alarmStore.OpenPageAsync(after, afterId, pageSize + 1, ct),
        pageSize, caller, ct);

      var closedSince = DateTime.UtcNow.AddMinutes(-window);
      var closed = await PageAsync(
        request.ClosedAfter,
        (after, afterId) => _alarmStore.ClosedPageAsync(closedSince, after, afterId, pageSize + 1, ct),
        pageSize, caller, ct);

      return new MapProto.AlarmList { Open = open, Closed = closed };
    }

    public override async Task<MapProto.Alarm> AcknowledgeAlarm(
      MapProto.AcknowledgeAlarmRequest request, ServerCallContext context)
    {
      var ct = context.CancellationToken;
      var caller = ToCaller(request.Caller);

      var alarm = await RequireVisibleAlarmAsync(request.Id, caller, ct);
      await RequireActionAsync(alarm, RightActions.Acknowledge, caller, ct);

      var outcome = await _alarmStore.AcknowledgeAsync(
        alarm.id, ActorOf(caller), NullIfEmpty(request.Comment), ct);

      return await ToAlarmAsync(await ResolveAsync(outcome, ct), caller, ct);
    }

    public override async Task<MapProto.Alarm> CloseAlarm(
      MapProto.CloseAlarmRequest request, ServerCallContext context)
    {
      var ct = context.CancellationToken;
      var caller = ToCaller(request.Caller);

      if (request.Reason is not (AlarmCloseReason.FalseAlarm or AlarmCloseReason.Other))
        throw new RpcException(new Status(
          StatusCode.InvalidArgument, "reason must be 'false_alarm' or 'other'"));

      var alarm = await RequireVisibleAlarmAsync(request.Id, caller, ct);
      await RequireActionAsync(alarm, RightActions.Acknowledge, caller, ct);

      var outcome = await _alarmStore.CloseAsync(
        alarm.id, ActorOf(caller), request.Reason, NullIfEmpty(request.Comment), ct);

      return await ToAlarmAsync(await ResolveAsync(outcome, ct), caller, ct);
    }

    // The system's own raise. Other services raise through their adapters inside VmsMap; this is
    // the contract that lets the entity be driven from outside and replayed in a test. It carries
    // no right check for the same reason UpdateStates carries none: Dapr invocation is the
    // boundary, and the caller is the deployment's own code.
    public override async Task<MapProto.Alarm> RaiseAlarm(
      MapProto.RaiseAlarmRequest request, ServerCallContext context)
    {
      var ct = context.CancellationToken;

      if (request.Source is not (AlarmSource.Sensor or AlarmSource.Retention or AlarmSource.Subscription))
        throw new RpcException(new Status(
          StatusCode.InvalidArgument, "source must be 'sensor', 'retention' or 'subscription'"));
      if (string.IsNullOrWhiteSpace(request.DedupKey))
        throw new RpcException(new Status(StatusCode.InvalidArgument, "dedup_key must not be empty"));
      if (Utils.ConvertObjectIdToGuid(request.ObjectId) is not { } objectId)
        throw new RpcException(new Status(StatusCode.InvalidArgument, "object_id must be a UUID"));
      if (!await _db.Markers.AsNoTracking().AnyAsync(m => m.id == objectId, ct))
        throw new RpcException(new Status(StatusCode.NotFound, "no such node"));

      var outcome = await _alarmStore.RaiseAsync(new RaiseAlarmSpec(
        ObjectId: objectId,
        Source: request.Source,
        DedupKey: request.DedupKey,
        ExpectedEndTs: request.ExpectedEndTs?.ToDateTime(),
        Severity: NullIfEmpty(request.Severity)), ct);

      return await ToAlarmAsync(
        await ResolveAsync(outcome, ct), ToCaller(request.Caller), ct);
    }

    // ── Internals ────────────────────────────────────────────────────────────────────────────────

    private delegate Task<List<DBAlarm>> PageReader(DateTime? after, Guid? afterId);

    private async Task<MapProto.AlarmPage> PageAsync(
      MapProto.AlarmCursor? cursor,
      PageReader read,
      int pageSize,
      CallerContext caller,
      CancellationToken ct)
    {
      // A cursor needs both halves: the pair is the key, and accepting one without the other
      // would start a page somewhere arbitrary.
      DateTime? after = cursor?.After is not null && !string.IsNullOrEmpty(cursor.AfterId)
        ? cursor.After.ToDateTime()
        : null;
      Guid? afterId = after is not null && Guid.TryParse(cursor!.AfterId, out var id) ? id : null;
      if (afterId is null)
        after = null;

      // One row more than the page tells whether there is another page without a second query.
      var rows = await read(after, afterId);
      var hasMore = rows.Count > pageSize;
      var page = hasMore ? rows.Take(pageSize).ToList() : rows;

      // Visibility is one batch for the page, and the acknowledgement right another: no row of a
      // list may cost its own walk up the tree.
      var ids = page.Select(a => Utils.ConvertGuidToObjectId(a.object_id)).ToList();
      var visible = await _rightsService.VisibleAsync(ids, caller, ct);
      var ackable = await _rightsService.AllowedAsync(ids, RightActions.Acknowledge, caller, ct);

      var visibleRows = page
        .Where(a => visible.Contains(Utils.ConvertGuidToObjectId(a.object_id)))
        .ToList();

      var meta = await MetadataAsync(visibleRows, ct);

      var result = new MapProto.AlarmPage { HasMore = hasMore };
      foreach (var row in visibleRows)
      {
        result.Alarms.Add(await ToAlarmAsync(
          row, caller, ct, ackable.Contains(Utils.ConvertGuidToObjectId(row.object_id)), meta));
      }

      if (hasMore && page.Count > 0)
      {
        var last = page[^1];
        var at = last.status == AlarmStatus.Closed ? last.closed_at : last.raised_at;
        if (at is not null)
        {
          result.Next = new MapProto.AlarmCursor
          {
            After = Timestamp.FromDateTime(DateTime.SpecifyKind(at.Value, DateTimeKind.Utc)),
            AfterId = last.id.ToString(),
          };
        }
      }

      return result;
    }

    /// <summary>The names and camera ids of a page's nodes, in one query each.</summary>
    private async Task<Dictionary<Guid, (string Name, string? CameraId)>> MetadataAsync(
      IReadOnlyCollection<DBAlarm> alarms, CancellationToken ct)
    {
      if (alarms.Count == 0)
        return [];

      var ids = alarms.Select(a => a.object_id).Distinct().ToList();

      var names = await _db.Markers.AsNoTracking()
        .Where(m => ids.Contains(m.id))
        .Select(m => new { m.id, m.name })
        .ToDictionaryAsync(m => m.id, m => m.name, ct);

      var cameras = await _db.DeviceNodes.AsNoTracking()
        .Where(n => ids.Contains(n.object_id))
        .Select(n => new { n.object_id, n.source_id })
        .ToDictionaryAsync(n => n.object_id, n => n.source_id, ct);

      return ids.ToDictionary(
        id => id,
        id => (names.GetValueOrDefault(id) ?? id.ToString(), cameras.GetValueOrDefault(id)));
    }

    private async Task<MapProto.Alarm> ToAlarmAsync(
      DBAlarm alarm,
      CallerContext caller,
      CancellationToken ct,
      bool? mayAcknowledge = null,
      Dictionary<Guid, (string Name, string? CameraId)>? metadata = null)
    {
      var permitted = mayAcknowledge
        ?? await _rightsService.IsAllowedAsync(
          Utils.ConvertGuidToObjectId(alarm.object_id), RightActions.Acknowledge, caller, ct);

      // The gate the store enforces, mirrored so the browser does not have to guess: an active
      // condition may only be acknowledged on a disarmed node.
      var armed = alarm.status == AlarmStatus.Active && alarm.condition_active
        && await _alarmStore.IsArmedAsync(alarm.object_id, ct);

      var canAck = permitted && alarm.status != AlarmStatus.Closed
        && (!alarm.condition_active || !armed);

      string? name = null;
      string? cameraId = null;
      if (metadata is null)
      {
        var meta = await MetadataAsync([alarm], ct);
        (name, cameraId) = meta.GetValueOrDefault(alarm.object_id);
        name ??= Utils.ConvertGuidToObjectId(alarm.object_id);
      }
      else
      {
        (name, cameraId) = metadata.GetValueOrDefault(alarm.object_id);
        name ??= Utils.ConvertGuidToObjectId(alarm.object_id);
      }

      return new MapProto.Alarm
      {
        Id = alarm.id.ToString(),
        ObjectId = Utils.ConvertGuidToObjectId(alarm.object_id),
        DefinitionId = alarm.definition_id?.ToString() ?? string.Empty,
        SubscriptionId = alarm.subscription_id?.ToString() ?? string.Empty,
        Source = alarm.source,
        DedupKey = alarm.dedup_key,
        ConditionActive = alarm.condition_active,
        Status = alarm.status,
        Severity = alarm.severity,
        RaisedAt = ToTimestamp(alarm.raised_at),
        ClearedAt = ToTimestamp(alarm.cleared_at),
        ExpectedEndTs = ToTimestamp(alarm.expected_end_ts),
        AcknowledgedAt = ToTimestamp(alarm.acknowledged_at),
        AcknowledgedBy = alarm.acknowledged_by ?? string.Empty,
        AckComment = alarm.ack_comment ?? string.Empty,
        ClosedAt = ToTimestamp(alarm.closed_at),
        CloseReason = alarm.close_reason ?? string.Empty,
        CloseComment = alarm.close_comment ?? string.Empty,
        ClosedBy = alarm.closed_by ?? string.Empty,
        SourceEventId = alarm.source_event_id?.ToString() ?? string.Empty,
        CanAck = canAck,
        ObjectName = name ?? string.Empty,
        CameraId = cameraId ?? string.Empty,
      };
    }

    private async Task<DBAlarm> RequireVisibleAlarmAsync(
      string id, CallerContext caller, CancellationToken ct)
    {
      var alarm = Guid.TryParse(id, out var guid) ? await _alarmStore.FindAsync(guid, ct) : null;
      if (alarm is null)
        throw new RpcException(new Status(StatusCode.NotFound, "no such alarm"));

      var objectId = Utils.ConvertGuidToObjectId(alarm.object_id);
      var visible = await _rightsService.VisibleAsync([objectId], caller, ct);
      if (!visible.Contains(objectId))
        throw new RpcException(new Status(StatusCode.NotFound, "no such alarm"));

      return alarm;
    }

    private async Task RequireActionAsync(
      DBAlarm alarm, string action, CallerContext caller, CancellationToken ct)
    {
      var allowed = await _rightsService.IsAllowedAsync(
        Utils.ConvertGuidToObjectId(alarm.object_id), action, caller, ct);
      if (!allowed)
        throw new RpcException(new Status(
          StatusCode.PermissionDenied, $"no '{action}' right on the alarm's node"));
    }

    private static async Task<DBAlarm> ResolveAsync(AlarmOutcome outcome, CancellationToken ct) =>
      outcome.Move switch
      {
        AlarmMove.NotFound => throw new RpcException(new Status(StatusCode.NotFound, "no such alarm")),
        AlarmMove.Refused => throw new RpcException(new Status(
          StatusCode.FailedPrecondition, outcome.Reason ?? "the transition is not allowed")),
        _ => outcome.Alarm ?? throw new RpcException(
          new Status(StatusCode.Internal, "the store returned no alarm")),
      };

    private static string ActorOf(CallerContext caller) =>
      string.IsNullOrWhiteSpace(caller.Username) ? "operator" : caller.Username!;

    private static string? NullIfEmpty(string? value) =>
      string.IsNullOrWhiteSpace(value) ? null : value;

    private static Timestamp? ToTimestamp(DateTime? value) =>
      value is null
        ? null
        : Timestamp.FromDateTime(DateTime.SpecifyKind(value.Value, DateTimeKind.Utc));
  }
}
