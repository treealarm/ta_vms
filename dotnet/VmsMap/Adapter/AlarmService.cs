using System;
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
  // Turns a retention episode into the camera's ALARM state and one journal event, and takes the
  // state away after the episode's projected end has passed and VmsFs confirms it did not renew.
  // The episode's own id is the event id, so a redelivered message cannot write a second event.
  internal sealed class AlarmService
  {
    private const string AlarmEventName = "alarm";
    private const string AlarmEndProperty = "alarm_end_ts";

    private readonly VmsMapDbContext _db;
    private readonly CameraStateWriter _stateWriter;
    private readonly IEventsService _eventsService;
    private readonly IRetentionEventSource _retention;
    private readonly ILogger<AlarmService> _logger;

    public AlarmService(
      VmsMapDbContext db,
      CameraStateWriter stateWriter,
      IEventsService eventsService,
      IRetentionEventSource retention,
      ILogger<AlarmService> logger)
    {
      _db = db ?? throw new ArgumentNullException(nameof(db));
      _stateWriter = stateWriter ?? throw new ArgumentNullException(nameof(stateWriter));
      _eventsService = eventsService ?? throw new ArgumentNullException(nameof(eventsService));
      _retention = retention ?? throw new ArgumentNullException(nameof(retention));
      _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Records one episode: ALARM if its projected end is still ahead, NORM if it is already over,
    /// and the journal event in both cases. State and event are one transaction.
    /// </summary>
    public async Task RecordAsync(
      string cameraId, RetentionEpisode episode, CancellationToken ct)
    {
      var active = episode.AlarmEndTs is null || episode.AlarmEndTs > DateTime.UtcNow;

      var ownsTransaction = _db.Database.IsRelational()
        && _db.Database.CurrentTransaction is null;
      await using var tx = ownsTransaction
        ? await _db.Database.BeginTransactionAsync(ct)
        : null;

      if (active)
        await _stateWriter.SetAlarmAsync(cameraId, ct);
      else
        await _stateWriter.ClearAlarmAsync(cameraId, ct);

      await UpsertEventAsync(cameraId, episode, ct);

      if (tx is not null)
        await tx.CommitAsync(ct);
    }

    /// <summary>
    /// The periodic repair: cameras still in ALARM whose recorded end is past are re-checked
    /// against VmsFs, because a renewal may have pushed the end forward. The check is
    /// idempotent, so every replica may run it.
    /// </summary>
    public async Task SweepAsync(CancellationToken ct)
    {
      var now = DateTime.UtcNow;

      var alarmedIds = await _db.ObjectStates.AsNoTracking()
        .Where(s => s.states.Any(v => v.state == CameraStateWriter.Alarm))
        .Select(s => s.id)
        .ToListAsync(ct);
      if (alarmedIds.Count == 0)
        return;

      var cameras = await _db.CameraNodes.AsNoTracking()
        .Where(n => alarmedIds.Contains(n.object_id))
        .Select(n => new { n.object_id, n.camera_id })
        .ToListAsync(ct);

      foreach (var camera in cameras)
      {
        try
        {
          var last = await _db.Events.AsNoTracking()
            .Include(e => e.extra_props)
            .Where(e => e.object_id == camera.object_id && e.event_name == AlarmEventName)
            .OrderByDescending(e => e.timestamp)
            .FirstOrDefaultAsync(ct);

          if (last is null)
            continue;

          var endText = last.extra_props
            .FirstOrDefault(p => p.prop_name == AlarmEndProperty)?.str_val;
          if (!DateTime.TryParse(
                endText, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var end)
              || end > now)
          {
            continue;
          }

          var episode = await _retention.FindAsync(camera.camera_id, last.timestamp, ct);

          // Still running, or extended: keep (or restore) the alarm and record the new end.
          if (episode is not null && (episode.AlarmEndTs is null || episode.AlarmEndTs >= now))
          {
            await _stateWriter.SetAlarmAsync(camera.camera_id, ct);
            if (episode.AlarmEndTs is not null)
              await SetEndAsync(last.id, episode.AlarmEndTs, ct);
            continue;
          }

          await _stateWriter.ClearAlarmAsync(camera.camera_id, ct);
          await SetEndAsync(last.id, episode?.AlarmEndTs ?? now, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
          throw;
        }
        catch (Exception ex)
        {
          // A VmsFs hiccup must not take the alarm down: the next sweep tries again.
          _logger.LogWarning(ex,
            "Alarm sweep failed for camera '{CameraId}', keeping its state", camera.camera_id);
        }
      }
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
        await _eventsService.InsertManyAsync([
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

    // The episode id VmsFs generated is the dedup key; a row from an older database may not have
    // one, and then a deterministic id keeps the message replayable just the same.
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
