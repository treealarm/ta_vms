using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using VmsMap.Db;
using VmsMap.Domain;

namespace VmsMap.Adapter
{
  /// <summary>
  /// Applies one <c>device-event</c>: the monotonicity guard, the part's state, the journal event
  /// and the cursor, all in one transaction.
  /// </summary>
  /// <remarks>
  /// Order comes from the publisher's <c>(epoch, seq)</c>, not from a clock. A redelivery or a
  /// swapped pair cannot turn the contact back: a message from the same process is accepted only
  /// with a greater sequence number, and a message from another process only if its own
  /// <c>observed_at</c> is not older than the last one accepted. The limitation is real and
  /// deliberate — if the new process's clock is behind the stored observation by more than the gap
  /// between events, its messages are refused until the clock catches up, and the log says so.
  /// </remarks>
  internal sealed class DeviceEventWriter
  {
    private readonly VmsMapDbContext _db;
    private readonly ObjectStateWriter _stateWriter;
    private readonly EventsWriter _eventsWriter;
    private readonly DeviceEventTranslator _translator;
    private readonly SensorAlarmService _sensorAlarms;
    private readonly ILogger<DeviceEventWriter> _logger;

    public DeviceEventWriter(
      VmsMapDbContext db,
      ObjectStateWriter stateWriter,
      EventsWriter eventsWriter,
      DeviceEventTranslator translator,
      SensorAlarmService sensorAlarms,
      ILogger<DeviceEventWriter> logger)
    {
      _db = db;
      _stateWriter = stateWriter;
      _eventsWriter = eventsWriter;
      _translator = translator;
      _sensorAlarms = sensorAlarms;
      _logger = logger;
    }

    public async Task ApplyAsync(string objectId, DeviceEventMessage message, CancellationToken ct)
    {
      var objectGuid = Utils.ConvertObjectIdToGuid(objectId);
      if (objectGuid is null)
        return;

      var ownsTransaction = _db.Database.IsRelational()
        && _db.Database.CurrentTransaction is null;
      await using var tx = ownsTransaction
        ? await _db.Database.BeginTransactionAsync(ct)
        : null;

      var node = await LockNodeAsync(objectGuid.Value, ct);
      if (node is null)
      {
        _logger.LogWarning(
          "device-event for node {ObjectId} has no device_node row — dropped", objectId);
        return;
      }

      if (!DeviceCursor.Accept(
            node.last_epoch, node.last_seq, node.last_observed_at,
            message.Epoch, message.Seq, message.ObservedAt))
      {
        _logger.LogWarning(
          "Dropped out-of-order device-event for node {ObjectId}: epoch {Epoch}, seq {Seq}, observed {ObservedAt} (stored epoch {LastEpoch}, seq {LastSeq}, observed {LastObserved})",
          objectId, message.Epoch, message.Seq, message.ObservedAt,
          node.last_epoch, node.last_seq, node.last_observed_at);
        if (tx is not null)
          await tx.CommitAsync(ct);
        return;
      }

      var change = _translator.Translate(message);
      var changed = false;

      if (change is not null)
      {
        changed = await _stateWriter.SetStatesAsync(objectId, [change.State], ct);

        // A repeat of the value already stored is not a transition; only a real change enters the
        // journal. The cursor still moves, so the next message is judged against this one.
        if (changed)
        {
          await _eventsWriter.WriteAsync([
            new EventDTO
            {
              id = EventIds.ForDeviceEvent(
                message.SourceKind!,
                message.SourceId!,
                message.PartKind!,
                message.Token!,
                message.Epoch!,
                message.Seq),
              object_id = objectId,
              event_name = change.EventName,
              event_priority = 0,
              timestamp = DateTime.UtcNow,
              param0 = change.Param0,
              extra_props = [.. change.ExtraProps],
            }
          ], ct);
        }
      }

      DeviceCursor.Advance(
        lastEpoch: node.last_epoch,
        setEpoch: value => node.last_epoch = value,
        setSeq: value => node.last_seq = value,
        setObservedAt: value => node.last_observed_at = value,
        message.Epoch!,
        message.Seq,
        message.ObservedAt);

      await _db.SaveChangesAsync(ct);

      if (tx is not null)
        await tx.CommitAsync(ct);

      // After the commit, not inside it: the arming comes from two other services, and a network
      // call must not happen with the part's row locked. The rule is idempotent, so applying it on
      // a repeat of the same value is how a lost message or a restart is repaired.
      if (change is not null)
      {
        var contact = DevicePayload.Text(message.Contact);
        await _sensorAlarms.ReconcileAsync(
          objectGuid.Value, message.SourceId!, message.PartKind!, message.Token!,
          contact, contact, ct);
      }

      if (change is null)
      {
        _logger.LogDebug(
          "device-event for node {ObjectId} carries no usable contact/active (polarity_known={Known}) — cursor advanced only",
          objectId, message.PolarityKnown);
      }
      else if (!changed)
      {
        _logger.LogDebug(
          "device-event for node {ObjectId} repeated state {State} — no journal event", objectId, change.State);
      }
    }

    private async Task<DBDeviceNode?> LockNodeAsync(Guid objectId, CancellationToken ct)
    {
      if (!_db.Database.IsRelational())
        return await _db.DeviceNodes.FirstOrDefaultAsync(n => n.object_id == objectId, ct);

      // FOR UPDATE serialises two deliveries of the same part: the second sees the first's cursor.
      return await _db.DeviceNodes
        .FromSqlInterpolated($"SELECT * FROM device_node WHERE object_id = {objectId} FOR UPDATE")
        .FirstOrDefaultAsync(ct);
    }
  }
}
