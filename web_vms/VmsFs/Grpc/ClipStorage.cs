using Google.Protobuf.WellKnownTypes;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using Vms.Fs;

namespace VmsFs;

internal sealed record RecordingWithEvents(
  Recording Recording,
  List<string> EventIds);

public sealed class ClipStorage(
  IDbContextFactory<ClipsDbContext> dbFactory,
  NpgsqlDataSource dataSource,
  CameraConfigCache configCache,
  FsActivityLog activityLog)
{
  private static readonly TimeSpan DefaultMaxRecordingRangeWallSpan = TimeSpan.FromMinutes(1);

  public async Task<string> IngestAsync(Recording clip, CancellationToken ct)
  {
    if (string.IsNullOrWhiteSpace(clip.CameraId) || string.IsNullOrWhiteSpace(clip.FilePath) || clip.StartTs is null)
      throw new ArgumentException("camera_id, file_path and start_ts are required");

    var clipUid = string.IsNullOrWhiteSpace(clip.ClipUid) ? Guid.NewGuid() : Guid.Parse(clip.ClipUid);
    var config = await configCache.GetAsync(clip.CameraId, ct, clip.StreamName);
    var retained = await ResolveRetainedAsync(clip, config, ct);
    var startTs = clip.StartTs.ToDateTime().ToUniversalTime();
    var endTs = clip.EndTs?.ToDateTime().ToUniversalTime();

    await using var cmd = dataSource.CreateCommand("""
      INSERT INTO clips (clip_uid, file_path, camera_id, stream_name, storage_id, start_ts, end_ts, size_bytes, retained)
      VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9)
      ON CONFLICT (file_path) DO UPDATE SET
        camera_id   = EXCLUDED.camera_id,
        stream_name = EXCLUDED.stream_name,
        storage_id  = EXCLUDED.storage_id,
        start_ts    = EXCLUDED.start_ts,
        end_ts      = COALESCE(EXCLUDED.end_ts, clips.end_ts),
        size_bytes  = GREATEST(clips.size_bytes, EXCLUDED.size_bytes),
        retained    = clips.retained OR EXCLUDED.retained
      RETURNING clip_uid::text;
      """);

    cmd.Parameters.AddWithValue(NpgsqlDbType.Uuid, clipUid);
    cmd.Parameters.AddWithValue(NpgsqlDbType.Text, clip.FilePath);
    cmd.Parameters.AddWithValue(NpgsqlDbType.Text, clip.CameraId);
    cmd.Parameters.AddWithValue(NpgsqlDbType.Text, clip.StreamName ?? string.Empty);
    cmd.Parameters.AddWithValue(NpgsqlDbType.Text, clip.StorageId ?? string.Empty);
    cmd.Parameters.AddWithValue(NpgsqlDbType.TimestampTz, startTs);
    cmd.Parameters.AddWithValue(NpgsqlDbType.TimestampTz, endTs is null ? DBNull.Value : endTs.Value);
    cmd.Parameters.AddWithValue(NpgsqlDbType.Bigint, (long)clip.SizeBytes);
    cmd.Parameters.AddWithValue(NpgsqlDbType.Boolean, retained);

    var returnedUid = (string)(await cmd.ExecuteScalarAsync(ct))!;
    activityLog.Record(new FsActivityEntry(
      Guid.TryParse(returnedUid, out var parsedUid) ? parsedUid : clipUid,
      clip.FilePath, clip.CameraId, clip.StreamName ?? "",
      retained, (long)clip.SizeBytes, DateTime.UtcNow, "ingested", endTs));
    return returnedUid;
  }

  // Returns the alarm_override_until timestamp (alarm_end + post_record_seconds).
  public async Task<DateTime> RetainEventAsync(RetainEventRequest req, CancellationToken ct)
  {
    var eventId = string.IsNullOrWhiteSpace(req.EventId)
      ? $"{req.CameraId}-{req.AlarmStartTs.ToDateTime().ToUniversalTime().Ticks}"
      : req.EventId.Trim();

    var alarmStart = req.AlarmStartTs.ToDateTime().ToUniversalTime();
    var timeout = req.TimeoutSeconds == 0 ? 30u : req.TimeoutSeconds;
    var alarmEnd = req.AlarmEndTs is not null
      ? req.AlarmEndTs.ToDateTime().ToUniversalTime()
      : alarmStart.AddSeconds(timeout);
    var streamName = req.HasStreamName && !string.IsNullOrWhiteSpace(req.StreamName)
      ? req.StreamName : null;

    var config = await configCache.GetAsync(req.CameraId, ct);
    var preSeconds  = config?.PreRecordSeconds  ?? 30;
    var postSeconds = config?.PostRecordSeconds ?? 30;

    await using var conn = await dataSource.OpenConnectionAsync(ct);
    await using var tx = await conn.BeginTransactionAsync(ct);

    // Check for an existing event with the same ID and lock it for the transaction.
    await using (var checkCmd = new NpgsqlCommand(
      "SELECT alarm_end_ts FROM retention_events WHERE event_id = $1 FOR UPDATE",
      conn, tx))
    {
      checkCmd.Parameters.AddWithValue(eventId);
      var existing = await checkCmd.ExecuteScalarAsync(ct);
      if (existing is DateTime existingEnd && existingEnd.ToUniversalTime() <= DateTime.UtcNow)
      {
        await tx.RollbackAsync(ct);
        throw new RetentionEventClosedException(eventId, existingEnd);
      }
    }

    await using (var upsert = new NpgsqlCommand("""
      INSERT INTO retention_events(event_id, camera_id, stream_name, alarm_start_ts, alarm_end_ts)
      VALUES($1, $2, $3, $4, COALESCE($5::timestamptz, $4 + ($6 * interval '1 second')))
      ON CONFLICT(event_id) DO UPDATE SET
        alarm_end_ts = GREATEST(retention_events.alarm_end_ts, EXCLUDED.alarm_end_ts);
      """, conn, tx))
    {
      upsert.Parameters.AddWithValue(eventId);
      upsert.Parameters.AddWithValue(req.CameraId);
      upsert.Parameters.AddWithValue((object?)streamName ?? DBNull.Value);
      upsert.Parameters.AddWithValue(alarmStart);
      upsert.Parameters.AddWithValue(req.AlarmEndTs is null ? DBNull.Value : req.AlarmEndTs.ToDateTime());
      upsert.Parameters.AddWithValue((int)timeout);
      await upsert.ExecuteNonQueryAsync(ct);
    }

    // When stream_name is specified retain only that stream's clips, otherwise all streams
    var streamFilter = streamName is null ? "" : "AND stream_name = $6";
    await using (var retainClips = new NpgsqlCommand($"""
      UPDATE clips
      SET retained = TRUE
      WHERE camera_id = $1
        AND start_ts <= $2 + ($5 * interval '1 second')
        AND COALESCE(end_ts, start_ts) >= $3 - ($4 * interval '1 second')
        {streamFilter};
      """, conn, tx))
    {
      retainClips.Parameters.AddWithValue(req.CameraId);
      retainClips.Parameters.AddWithValue(alarmEnd);
      retainClips.Parameters.AddWithValue(alarmStart);
      retainClips.Parameters.AddWithValue(preSeconds);
      retainClips.Parameters.AddWithValue(postSeconds);
      if (streamName is not null)
        retainClips.Parameters.AddWithValue(NpgsqlDbType.Text, streamName);
      await retainClips.ExecuteNonQueryAsync(ct);
    }

    await tx.CommitAsync(ct);
    return alarmEnd.AddSeconds(postSeconds);
  }

  public async Task<List<Recording>> GetRecordingsFromToAsync(GetRecordingsFromToRequest request, CancellationToken ct)
  {
    var limit = request.Limit == 0 ? 100 : (int)request.Limit;
    var streamName = request.HasStreamName ? request.StreamName : null;

    await using var db = await dbFactory.CreateDbContextAsync(ct);
    var q = db.Clips.Where(c => c.CameraId == request.CameraId && c.Retained);

    if (!string.IsNullOrEmpty(streamName))
      q = q.Where(c => c.StreamName == streamName);

    if (request.FromTs != null)
    {
      var from = request.FromTs.ToDateTime().ToUniversalTime();
      q = q.Where(c => (c.EndTs ?? c.StartTs.AddMinutes(1)) >= from);
    }
    if (request.ToTs != null)
    {
      var to = request.ToTs.ToDateTime().ToUniversalTime();
      q = q.Where(c => c.StartTs <= to);
    }

    var rows = await q.OrderByDescending(c => c.StartTs).Take(limit).ToListAsync(ct);
    return rows.Select(ToRecording).ToList();
  }

  public async Task<List<Recording>> GetRecordingsAroundAsync(GetRecordingsAroundRequest request, CancellationToken ct)
  {
    var limit = request.Limit == 0 ? 100 : (int)request.Limit;
    var beforeLimit = limit / 2;
    var afterLimit = limit - beforeLimit;
    var center = request.Timestamp.ToDateTime().ToUniversalTime();
    var streamName = request.HasStreamName ? request.StreamName : null;

    await using var db = await dbFactory.CreateDbContextAsync(ct);

    var beforeQ = db.Clips.Where(c => c.CameraId == request.CameraId && c.Retained && c.StartTs < center);
    var afterQ = db.Clips.Where(c => c.CameraId == request.CameraId && c.Retained && c.StartTs >= center);

    if (!string.IsNullOrEmpty(streamName))
    {
      beforeQ = beforeQ.Where(c => c.StreamName == streamName);
      afterQ = afterQ.Where(c => c.StreamName == streamName);
    }

    var before = await beforeQ.OrderByDescending(c => c.StartTs).Take(beforeLimit).ToListAsync(ct);
    var after = await afterQ.OrderBy(c => c.StartTs).Take(afterLimit).ToListAsync(ct);

    var combined = new List<Recording>(before.Count + after.Count);
    for (var i = before.Count - 1; i >= 0; i--)
      combined.Add(ToRecording(before[i]));
    combined.AddRange(after.Select(ToRecording));
    return combined;
  }

  public async Task<List<RecordingRange>> GetRecordingRangesAsync(GetRecordingRangesRequest request, CancellationToken ct)
  {
    var limit = request.Limit == 0 ? 100 : (int)request.Limit;
    var gapMs = request.GapToleranceMs <= 0 ? 1000 : (int)request.GapToleranceMs;
    var gap = TimeSpan.FromMilliseconds(gapMs);
    var streamName = request.HasStreamName ? request.StreamName : null;

    List<RecordingWithEvents> clips;
    var fetchLimit = Math.Max(limit * 10, limit);
    if (request.Timestamp != null)
    {
      var ts = request.Timestamp.ToDateTime().ToUniversalTime();
      clips = await GetRecordingsAroundWithEventsAsync(request.CameraId, streamName, ts, fetchLimit, ct);
    }
    else
    {
      clips = await GetRecordingsWithEventsAsync(request.CameraId, streamName, request.FromTs, request.ToTs, fetchLimit, ct);
    }

    // Group clips by stream first so ranges never mix multiple streams,
    // then merge all streams' ranges sorted by their earliest clip time.
    var ranges = clips
      .GroupBy(c => c.Recording.StreamName)
      .SelectMany(g => GroupRecordingRanges(g.OrderBy(c => c.Recording.StartTs.ToDateTime()).ToList(), gap, DefaultMaxRecordingRangeWallSpan))
      .OrderBy(r => r.Recordings[0].StartTs.ToDateTime())
      .ToList();

    if (ranges.Count > limit)
      ranges = ranges.GetRange(0, limit);
    return ranges;
  }

  public async Task<List<Recording>> GetClipsFromAsync(GetClipsFromRequest request, CancellationToken ct)
  {
    var count = request.Count == 0 ? 1 : (int)request.Count;
    var anchorUid = Guid.Parse(request.StartClipUid);
    var streamName = request.HasStreamName ? request.StreamName : null;

    await using var db = await dbFactory.CreateDbContextAsync(ct);

    var anchor = await db.Clips
      .Where(c => c.CameraId == request.CameraId && c.ClipUid == anchorUid)
      .Select(c => new { c.StartTs, c.StreamName })
      .FirstOrDefaultAsync(ct);

    if (anchor is null)
      return [];

    // Use explicit stream filter if provided, otherwise inherit the anchor clip's stream
    // so playback never crosses stream boundaries.
    var effectiveStream = !string.IsNullOrEmpty(streamName) ? streamName : anchor.StreamName;

    var q = db.Clips.Where(c => c.CameraId == request.CameraId && c.Retained && c.StartTs >= anchor.StartTs);

    if (!string.IsNullOrEmpty(effectiveStream))
      q = q.Where(c => c.StreamName == effectiveStream);

    var rows = await q.OrderBy(c => c.StartTs).Take(count).ToListAsync(ct);
    return rows.Select(ToRecording).ToList();
  }

  public async Task<List<(Guid ClipUid, string FilePath, string CameraId, string StorageId, DateTime EndTs)>> GetUnretainedCandidatesAsync(
    int minOlderThanSeconds,
    int batchSize,
    CancellationToken ct)
  {
    var threshold = DateTime.UtcNow.AddSeconds(-minOlderThanSeconds);

    await using var db = await dbFactory.CreateDbContextAsync(ct);
    var rows = await db.Clips
      .Where(c => !c.Retained && c.EndTs != null && c.EndTs < threshold)
      .OrderBy(c => c.StartTs)
      .Take(batchSize)
      .Select(c => new { c.ClipUid, c.FilePath, c.CameraId, c.StorageId, EndTs = c.EndTs!.Value })
      .ToListAsync(ct);

    return rows.Select(r => (r.ClipUid, r.FilePath, r.CameraId, r.StorageId, r.EndTs)).ToList();
  }

  public async Task<List<Recording>> GetRecordingsByUidsAsync(IReadOnlyList<Guid> clipUids, CancellationToken ct)
  {
    if (clipUids.Count == 0)
      return [];

    await using var db = await dbFactory.CreateDbContextAsync(ct);
    var rows = await db.Clips
      .Where(c => clipUids.Contains(c.ClipUid))
      .ToListAsync(ct);

    return rows.Select(ToRecording).ToList();
  }

  public async Task DeleteClipRecordAsync(Guid clipUid, CancellationToken ct)
  {
    await using var db = await dbFactory.CreateDbContextAsync(ct);
    await db.Clips.Where(c => c.ClipUid == clipUid).ExecuteDeleteAsync(ct);
  }

  // Returns null when the event does not exist; empty list when event exists but has no retained clips yet.
  public async Task<List<Recording>?> GetClipsForEventAsync(string eventId, CancellationToken ct)
  {
    await using var evtCmd = dataSource.CreateCommand("""
      SELECT camera_id FROM retention_events WHERE event_id = $1;
      """);
    evtCmd.Parameters.AddWithValue(eventId);
    var cameraId = (string?)await evtCmd.ExecuteScalarAsync(ct);
    if (cameraId is null)
      return null;

    var config = await configCache.GetAsync(cameraId, ct);
    var pre  = config?.PreRecordSeconds  ?? 30;
    var post = config?.PostRecordSeconds ?? 30;

    await using var cmd = dataSource.CreateCommand("""
      SELECT c.camera_id, c.stream_name, c.file_path, c.start_ts, c.end_ts, c.size_bytes, c.clip_uid::text, c.storage_id,
             ''
      FROM clips c
      JOIN retention_events re ON re.event_id = $1
        AND re.camera_id = c.camera_id
        AND c.start_ts <= re.alarm_end_ts + ($2 * interval '1 second')
        AND COALESCE(c.end_ts, c.start_ts) >= re.alarm_start_ts - ($3 * interval '1 second')
      WHERE c.retained = TRUE
      ORDER BY c.stream_name, c.start_ts;
      """);
    cmd.Parameters.AddWithValue(eventId);
    cmd.Parameters.AddWithValue(post);
    cmd.Parameters.AddWithValue(pre);

    var rows = await ReadRecordingWithEventsAsync(cmd, ct);
    return rows.Select(r => r.Recording).ToList();
  }

  public async Task<bool> HasActiveRetentionWindowAsync(string cameraId, CancellationToken ct)
  {
    var config = await configCache.GetAsync(cameraId, ct);
    var post = config?.PostRecordSeconds ?? 30;

    await using var cmd = dataSource.CreateCommand("""
      SELECT EXISTS (
        SELECT 1 FROM retention_events
        WHERE camera_id = $1
          AND alarm_start_ts <= now()
          AND alarm_end_ts + ($2 * interval '1 second') >= now()
      );
      """);
    cmd.Parameters.AddWithValue(cameraId);
    cmd.Parameters.AddWithValue(post);
    return (bool)(await cmd.ExecuteScalarAsync(ct))!;
  }

  public async Task<(List<RetentionEventRow> Events, bool HasMore, Timestamp? NextBefore)> ListRetentionEventsAsync(
    uint limit,
    Timestamp? beforeAlarmStart,
    CancellationToken ct)
  {
    var lim = limit == 0 || limit > 200 ? 50 : (int)limit;

    await using var cmd = beforeAlarmStart is null
      ? dataSource.CreateCommand("""
        SELECT event_id, camera_id, stream_name, alarm_start_ts, alarm_end_ts
        FROM retention_events
        ORDER BY alarm_start_ts DESC, event_id DESC
        LIMIT $1;
        """)
      : dataSource.CreateCommand("""
        SELECT event_id, camera_id, stream_name, alarm_start_ts, alarm_end_ts
        FROM retention_events
        WHERE alarm_start_ts < $1
        ORDER BY alarm_start_ts DESC, event_id DESC
        LIMIT $2;
        """);

    if (beforeAlarmStart is null)
      cmd.Parameters.AddWithValue(lim);
    else
    {
      cmd.Parameters.AddWithValue(beforeAlarmStart.ToDateTime());
      cmd.Parameters.AddWithValue(lim);
    }

    var events = new List<RetentionEventRow>();
    await using var reader = await cmd.ExecuteReaderAsync(ct);
    while (await reader.ReadAsync(ct))
    {
      var row = new RetentionEventRow
      {
        EventId      = reader.GetString(0),
        CameraId     = reader.GetString(1),
        AlarmStartTs = Timestamp.FromDateTime(reader.GetDateTime(3).ToUniversalTime())
      };
      if (!reader.IsDBNull(2)) row.StreamName = reader.GetString(2);
      if (!reader.IsDBNull(4)) row.AlarmEndTs = Timestamp.FromDateTime(reader.GetDateTime(4).ToUniversalTime());
      events.Add(row);
    }

    var hasMore = events.Count == lim && lim > 0;
    Timestamp? nextBefore = null;
    if (hasMore && events.Count > 0)
      nextBefore = events[^1].AlarmStartTs;

    return (events, hasMore, nextBefore);
  }

  private async Task<List<RecordingWithEvents>> GetRecordingsWithEventsAsync(
    string cameraId,
    string? streamName,
    Timestamp? fromTs,
    Timestamp? toTs,
    int limit,
    CancellationToken ct)
  {
    var config = await configCache.GetAsync(cameraId, ct);
    var pre = config?.PreRecordSeconds ?? 30;
    var post = config?.PostRecordSeconds ?? 30;

    var streamFilter = string.IsNullOrEmpty(streamName) ? "" : "AND c.stream_name = $7";
    await using var cmd = dataSource.CreateCommand($"""
      SELECT c.camera_id, c.stream_name, c.file_path, c.start_ts, c.end_ts, c.size_bytes, c.clip_uid::text, c.storage_id,
             COALESCE(string_agg(DISTINCT re.event_id, ','), '')
      FROM clips c
      LEFT JOIN retention_events re ON re.camera_id = c.camera_id
        AND c.start_ts <= re.alarm_end_ts + ($5 * interval '1 second')
        AND COALESCE(c.end_ts, c.start_ts) >= re.alarm_start_ts - ($6 * interval '1 second')
      WHERE c.camera_id = $1 AND c.retained = TRUE {streamFilter}
        AND ($2::timestamptz IS NULL OR COALESCE(c.end_ts, c.start_ts + interval '1 minute') >= $2)
        AND ($3::timestamptz IS NULL OR c.start_ts <= $3)
      GROUP BY c.clip_uid
      ORDER BY c.start_ts ASC
      LIMIT $4;
      """);

    cmd.Parameters.AddWithValue(cameraId);
    cmd.Parameters.AddWithValue(NpgsqlDbType.TimestampTz, fromTs is null ? DBNull.Value : fromTs.ToDateTime());
    cmd.Parameters.AddWithValue(NpgsqlDbType.TimestampTz, toTs is null ? DBNull.Value : toTs.ToDateTime());
    cmd.Parameters.AddWithValue(limit);
    cmd.Parameters.AddWithValue(post);
    cmd.Parameters.AddWithValue(pre);
    if (!string.IsNullOrEmpty(streamName))
      cmd.Parameters.AddWithValue(NpgsqlDbType.Text, streamName);

    return await ReadRecordingWithEventsAsync(cmd, ct);
  }

  private async Task<List<RecordingWithEvents>> GetRecordingsAroundWithEventsAsync(
    string cameraId,
    string? streamName,
    DateTime center,
    int limit,
    CancellationToken ct)
  {
    var config = await configCache.GetAsync(cameraId, ct);
    var pre = config?.PreRecordSeconds ?? 30;
    var post = config?.PostRecordSeconds ?? 30;

    var beforeLimit = limit / 2;
    var afterLimit = limit - beforeLimit;

    var before = await QueryRecordingsWithEventsAsync(cameraId, streamName, center, beforeLimit, post, pre, before: true, ct);
    var after = await QueryRecordingsWithEventsAsync(cameraId, streamName, center, afterLimit, post, pre, before: false, ct);

    var combined = new List<RecordingWithEvents>(before.Count + after.Count);
    for (var i = before.Count - 1; i >= 0; i--)
      combined.Add(before[i]);
    combined.AddRange(after);
    return combined;
  }

  private async Task<List<RecordingWithEvents>> QueryRecordingsWithEventsAsync(
    string cameraId,
    string? streamName,
    DateTime center,
    int limit,
    int post,
    int pre,
    bool before,
    CancellationToken ct)
  {
    var timeOp = before ? "<" : ">=";
    var order = before ? "DESC" : "ASC";
    var streamFilter = string.IsNullOrEmpty(streamName) ? "" : "AND c.stream_name = $6";

    await using var cmd = dataSource.CreateCommand($"""
      SELECT c.camera_id, c.stream_name, c.file_path, c.start_ts, c.end_ts, c.size_bytes, c.clip_uid::text, c.storage_id,
             COALESCE(string_agg(DISTINCT re.event_id, ','), '')
      FROM clips c
      LEFT JOIN retention_events re ON re.camera_id = c.camera_id
        AND c.start_ts <= re.alarm_end_ts + ($4 * interval '1 second')
        AND COALESCE(c.end_ts, c.start_ts) >= re.alarm_start_ts - ($5 * interval '1 second')
      WHERE c.camera_id = $1 AND c.retained = TRUE AND c.start_ts {timeOp} $2 {streamFilter}
      GROUP BY c.clip_uid
      ORDER BY c.start_ts {order}
      LIMIT $3;
      """);

    cmd.Parameters.AddWithValue(cameraId);
    cmd.Parameters.AddWithValue(center);
    cmd.Parameters.AddWithValue(limit);
    cmd.Parameters.AddWithValue(post);
    cmd.Parameters.AddWithValue(pre);
    if (!string.IsNullOrEmpty(streamName))
      cmd.Parameters.AddWithValue(NpgsqlDbType.Text, streamName);

    return await ReadRecordingWithEventsAsync(cmd, ct);
  }

  private static async Task<List<RecordingWithEvents>> ReadRecordingWithEventsAsync(NpgsqlCommand command, CancellationToken ct)
  {
    var list = new List<RecordingWithEvents>();
    await using var reader = await command.ExecuteReaderAsync(ct);
    while (await reader.ReadAsync(ct))
    {
      var rec = new Recording
      {
        CameraId   = reader.GetString(0),
        StreamName = reader.GetString(1),
        FilePath   = reader.GetString(2),
        StartTs    = Timestamp.FromDateTime(reader.GetDateTime(3).ToUniversalTime()),
        SizeBytes  = (ulong)reader.GetInt64(5),
        ClipUid    = reader.IsDBNull(6) ? "" : reader.GetString(6),
        StorageId  = reader.IsDBNull(7) ? "" : reader.GetString(7)
      };
      if (!reader.IsDBNull(4))
        rec.EndTs = Timestamp.FromDateTime(reader.GetDateTime(4).ToUniversalTime());

      var ev = reader.IsDBNull(8) ? "" : reader.GetString(8);
      var ids = string.IsNullOrEmpty(ev)
        ? new List<string>()
        : ev.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct().ToList();
      list.Add(new RecordingWithEvents(rec, ids));
    }
    return list;
  }

  private static List<RecordingRange> GroupRecordingRanges(
    List<RecordingWithEvents> clips,
    TimeSpan gapTolerance,
    TimeSpan maxRangeWallSpan)
  {
    const int degenerateMaxInterStartSeconds = 65;

    var ranges = new List<RecordingRange>();
    RecordingWithEvents? prevItem = null;

    static DateTime AsUtc(DateTime t) =>
      t.Kind switch
      {
        DateTimeKind.Utc => t,
        DateTimeKind.Local => t.ToUniversalTime(),
        _ => DateTime.SpecifyKind(t, DateTimeKind.Utc).ToUniversalTime()
      };

    foreach (var item in clips)
    {
      var clip = item.Recording;
      var start = AsUtc(clip.StartTs.ToDateTime());

      if (ranges.Count == 0)
      {
        var r = new RecordingRange();
        r.Recordings.Add(clip);
        ranges.Add(r);
        prevItem = item;
        continue;
      }

      var cur = ranges[^1];
      var rangeFirstStart = AsUtc(cur.Recordings[0].StartTs.ToDateTime());
      var spanExceeded = maxRangeWallSpan > TimeSpan.Zero && start - rangeFirstStart >= maxRangeWallSpan;

      var prevRec = prevItem!.Recording;
      var prevStart = AsUtc(prevRec.StartTs.ToDateTime());
      var prevEndRaw = prevRec.EndTs?.ToDateTime();
      var prevHasWallEnd = prevEndRaw.HasValue && AsUtc(prevEndRaw.Value) > prevStart;
      var prevWallEnd = prevHasWallEnd ? AsUtc(prevEndRaw!.Value) : prevStart;

      bool pauseTooLong;
      if (prevHasWallEnd)
        pauseTooLong = start - prevWallEnd > gapTolerance;
      else
        pauseTooLong = start - prevStart > TimeSpan.FromSeconds(degenerateMaxInterStartSeconds);

      if (spanExceeded || pauseTooLong)
      {
        var r = new RecordingRange();
        r.Recordings.Add(clip);
        ranges.Add(r);
      }
      else
      {
        cur.Recordings.Add(clip);
      }

      prevItem = item;
    }

    return ranges;
  }

  private async Task<bool> ResolveRetainedAsync(Recording clip, CameraConfig? config, CancellationToken ct)
  {
    if (config is null)
      return true;

    if (config.RecordingMode == Vms.Cfg.RecordingMode.RecordingContinuous)
      return true;

    // For prealarm and off modes, check retention_events overlap.
    // Off-mode cameras can have alarm-triggered clips if an override was just activated;
    // checking retention_events here covers that race and is the correct semantic anyway.
    var startTs = clip.StartTs.ToDateTime();
    var endTs = clip.EndTs is not null ? clip.EndTs.ToDateTime() : (DateTime?)null;
    // Off-mode cameras have no configured pre/post; use generous defaults so alarm-triggered
    // clips (which start a few seconds after the alarm fires) are correctly captured.
    var pre  = config.PreRecordSeconds  > 0 ? config.PreRecordSeconds  : 30;
    var post = config.PostRecordSeconds > 0 ? config.PostRecordSeconds : 30;

    await using var cmd = dataSource.CreateCommand("""
      SELECT EXISTS (
        SELECT 1 FROM retention_events
        WHERE camera_id = $1
          AND $2::timestamptz <= alarm_end_ts + ($4 * interval '1 second')
          AND COALESCE($3::timestamptz, $2::timestamptz) >= alarm_start_ts - ($5 * interval '1 second')
      );
      """);
    cmd.Parameters.AddWithValue(clip.CameraId);
    cmd.Parameters.AddWithValue(NpgsqlDbType.TimestampTz, startTs);
    cmd.Parameters.AddWithValue(NpgsqlDbType.TimestampTz, endTs.HasValue ? endTs.Value : DBNull.Value);
    cmd.Parameters.AddWithValue(post);
    cmd.Parameters.AddWithValue(pre);
    return (bool)(await cmd.ExecuteScalarAsync(ct))!;
  }

  private static Recording ToRecording(DbClip c) => new()
  {
    CameraId   = c.CameraId,
    StreamName = c.StreamName,
    FilePath   = c.FilePath,
    StartTs    = Timestamp.FromDateTime(c.StartTs.ToUniversalTime()),
    EndTs      = c.EndTs.HasValue ? Timestamp.FromDateTime(c.EndTs.Value.ToUniversalTime()) : null,
    SizeBytes  = (ulong)c.SizeBytes,
    ClipUid    = c.ClipUid.ToString(),
    StorageId  = c.StorageId
  };
}

public sealed class RetentionEventClosedException(string eventId, DateTime closedAt)
  : Exception($"Retention event '{eventId}' is already closed (alarm_end_ts={closedAt:O})")
{
  public string EventId { get; } = eventId;
  public DateTime ClosedAt { get; } = closedAt;
}
