using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using VmsMap.Clients;
using VmsMap.Db;
using VmsMap.Domain;

namespace VmsMap.Adapter
{
  // Turns a retention episode into an alarm on the camera's node and one journal event, and takes
  // it away after the episode's projected end has passed and VmsFs confirms it did not renew. The
  // episode's own id is the event id and the alarm's dedup key, so a redelivered message can
  // neither write a second event nor raise a second alarm.
  //
  // The legacy `alarm` event (id = event_uuid, props alarm_end_ts) stays: the retention journal is
  // built on it. The alarm entity writes no `alarm_raised` for this source — the row is where the
  // operator sees it — but an auto-on-normal close is an `alarm_closed` like any other.
  internal sealed class AlarmService
  {
    private const string AlarmEventName = "alarm";
    private const string AlarmEndProperty = "alarm_end_ts";

    private readonly VmsMapDbContext _db;
    private readonly AlarmStore _alarms;
    private readonly EventsWriter _eventsWriter;
    private readonly IRetentionEventSource _retention;
    private readonly ILogger<AlarmService> _logger;

    public AlarmService(
      VmsMapDbContext db,
      AlarmStore alarms,
      EventsWriter eventsWriter,
      IRetentionEventSource retention,
      ILogger<AlarmService> logger)
    {
      _db = db ?? throw new ArgumentNullException(nameof(db));
      _alarms = alarms ?? throw new ArgumentNullException(nameof(alarms));
      _eventsWriter = eventsWriter ?? throw new ArgumentNullException(nameof(eventsWriter));
      _retention = retention ?? throw new ArgumentNullException(nameof(retention));
      _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Records one episode: an alarm if its projected end is still ahead, and the journal event in
    /// both cases. State, alarm row and event are one transaction.
    /// </summary>
    public async Task RecordAsync(
      string cameraId, RetentionEpisode episode, CancellationToken ct)
    {
      // Footage, not an alarm: the episode stays in VmsFs and in the clip list, and neither ALARM
      // nor a row nor the legacy `alarm` event is written for it.
      if (!episode.RaiseAlarm)
        return;

      var objectId = CameraObjectId.For(cameraId);
      var dedupKey = $"retention:{EventId(cameraId, episode):D}";
      var active = episode.AlarmEndTs is null || episode.AlarmEndTs > DateTime.UtcNow;

      var ownsTransaction = _db.Database.IsRelational()
        && _db.Database.CurrentTransaction is null;
      await using var tx = ownsTransaction
        ? await _db.Database.BeginTransactionAsync(ct)
        : null;

      if (active)
      {
        await _alarms.RaiseAsync(new RaiseAlarmSpec(
          ObjectId: Guid.Parse(objectId),
          Source: AlarmSource.Retention,
          DedupKey: dedupKey,
          ExpectedEndTs: episode.AlarmEndTs,
          SourceEventId: Guid.TryParse(episode.EventUuid, out var episodeId) ? episodeId : null,
          Trigger: episode.Trigger), ct);
      }
      else
      {
        await _alarms.NormalAsync(Guid.Parse(objectId), dedupKey, ct);
      }

      await UpsertEventAsync(cameraId, episode, ct);

      if (tx is not null)
        await tx.CommitAsync(ct);
    }

    /// <summary>
    /// The periodic repair: open retention alarms whose recorded end is past are re-checked against
    /// VmsFs, because a renewal may have pushed the end forward. An alarm is the entity that says
    /// what is open, not the ALARM state — the state may have been taken away for another reason.
    /// The check is idempotent, so every replica may run it.
    /// </summary>
    public async Task SweepAsync(CancellationToken ct)
    {
      var now = DateTime.UtcNow;

      var open = await _db.Alarms.AsNoTracking()
        .Where(a => a.source == AlarmSource.Retention && a.condition_active)
        .Select(a => new { a.object_id, a.dedup_key, a.source_event_id, a.expected_end_ts })
        .ToListAsync(ct);

      foreach (var alarm in open)
      {
        try
        {
          if (alarm.expected_end_ts is { } projected && projected > now)
            continue; // still running by its own projection; nothing to repair

          var cameraId = await CameraIdAsync(alarm.object_id, ct);
          if (cameraId is null)
            continue;

          // The legacy event carries the episode's start; the alarm's dedup key is its id.
          var episodeStart = await EpisodeStartAsync(alarm.source_event_id, ct);
          if (episodeStart is null)
            continue;

          var episode = await _retention.FindAsync(cameraId, episodeStart.Value, ct);

          // Still running, or extended: keep (or restore) the alarm and record the new end.
          if (episode is not null && (episode.AlarmEndTs is null || episode.AlarmEndTs >= now))
          {
            await _alarms.RaiseAsync(new RaiseAlarmSpec(
              ObjectId: alarm.object_id,
              Source: AlarmSource.Retention,
              DedupKey: alarm.dedup_key,
              ExpectedEndTs: episode.AlarmEndTs,
              SourceEventId: alarm.source_event_id,
              Trigger: episode.Trigger), ct);

            if (episode.AlarmEndTs is not null && alarm.source_event_id is { } eventId)
              await SetEndAsync(eventId, episode.AlarmEndTs, ct);
            continue;
          }

          await _alarms.NormalAsync(alarm.object_id, alarm.dedup_key, ct);
          if (alarm.source_event_id is { } closedId)
            await SetEndAsync(closedId, episode?.AlarmEndTs ?? now, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
          throw;
        }
        catch (Exception ex)
        {
          // A VmsFs hiccup must not take the alarm down: the next sweep tries again.
          _logger.LogWarning(ex,
            "Alarm sweep failed for node {ObjectId}, keeping its alarm", alarm.object_id);
        }
      }
    }

    private async Task<string?> CameraIdAsync(Guid objectId, CancellationToken ct) =>
      await _db.DeviceNodes.AsNoTracking()
        .Where(n => n.object_id == objectId)
        .Select(n => n.source_id)
        .FirstOrDefaultAsync(ct);

    private async Task<DateTime?> EpisodeStartAsync(Guid? eventId, CancellationToken ct)
    {
      if (eventId is null)
        return null;

      return await _db.Events.AsNoTracking()
        .Where(e => e.id == eventId.Value && e.event_name == AlarmEventName)
        .Select(e => (DateTime?)e.timestamp)
        .FirstOrDefaultAsync(ct);
    }

    private async Task UpsertEventAsync(
      string cameraId, RetentionEpisode episode, CancellationToken ct)
    {
      var canonicalId = CameraObjectId.For(cameraId);
      var eventId = EventId(cameraId, episode);

      var existing = await _db.Events
        .Include(e => e.extra_props)
        .FirstOrDefaultAsync(e => e.id == eventId, ct);

      var endText = FormatEnd(episode.AlarmEndTs);

      if (existing is null)
      {
        await _eventsWriter.WriteAsync([
          new EventDTO
          {
            id = eventId.ToString(),
            object_id = canonicalId,
            event_name = AlarmEventName,
            event_priority = 0,
            timestamp = DateTime.SpecifyKind(episode.AlarmStartTs, DateTimeKind.Utc),
            param0 = episode.StreamName ?? string.Empty,
            param1 = episode.Trigger ?? string.Empty,
            extra_props =
            [
              new ObjExtraPropertyDTO
              {
                prop_name = AlarmEndProperty,
                str_val = endText,
                visual_type = VisualTypes.DateTime,
              }
            ],
          }
        ]);
        return;
      }

      // A renewal: the episode keeps its id, only the projected end moves.
      var prop = existing.extra_props.FirstOrDefault(p => p.prop_name == AlarmEndProperty);
      if (prop is null)
      {
        existing.extra_props.Add(new EventProp
        {
          id = Utils.ConvertObjectIdToGuid(ObjectIdGenerator.GenerateObjectId(
            existing.id + AlarmEndProperty, "event_prop"))!.Value,
          owner_id = existing.id,
          prop_name = AlarmEndProperty,
          str_val = endText,
          visual_type = VisualTypes.DateTime,
        });
      }
      else
      {
        prop.str_val = endText;
      }

      await _db.SaveChangesAsync(ct);
    }

    private async Task SetEndAsync(Guid eventId, DateTime? end, CancellationToken ct)
    {
      var evt = await _db.Events
        .Include(e => e.extra_props)
        .FirstOrDefaultAsync(e => e.id == eventId, ct);
      if (evt is null)
        return;

      var text = FormatEnd(end);
      var prop = evt.extra_props.FirstOrDefault(p => p.prop_name == AlarmEndProperty);
      if (prop is null)
      {
        evt.extra_props.Add(new EventProp
        {
          id = Utils.ConvertObjectIdToGuid(ObjectIdGenerator.GenerateObjectId(
            evt.id + AlarmEndProperty, "event_prop"))!.Value,
          owner_id = evt.id,
          prop_name = AlarmEndProperty,
          str_val = text,
          visual_type = VisualTypes.DateTime,
        });
      }
      else
      {
        prop.str_val = text;
      }

      await _db.SaveChangesAsync(ct);
    }

    // The episode id VmsFs generated is the event id and the dedup key; a row from an older
    // database may not have one, and then a deterministic id keeps the message replayable just the
    // same.
    private static Guid EventId(string cameraId, RetentionEpisode episode) =>
      Guid.TryParse(episode.EventUuid, out var id)
        ? id
        : Guid.Parse(ObjectIdGenerator.GenerateObjectId(
            $"{cameraId}|{episode.AlarmStartTs:O}", "alarm_event"));

    private static string FormatEnd(DateTime? end) =>
      end is null
        ? string.Empty
        : DateTime.SpecifyKind(end.Value, DateTimeKind.Utc).ToString("O", CultureInfo.InvariantCulture);
  }
}
