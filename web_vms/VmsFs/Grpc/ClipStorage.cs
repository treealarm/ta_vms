using Google.Protobuf.WellKnownTypes;
using Npgsql;
using NpgsqlTypes;
using Vms.Fs;

namespace VmsFs;

internal sealed record RecordingWithEvents(
  Recording Recording,
  List<string> EventIds);

public sealed class ClipStorage(NpgsqlDataSource dataSource)
{
  private readonly NpgsqlDataSource _dataSource = dataSource;

  /// <summary>Split uninterrupted spans into ~minute-sized ranges for the archive list.</summary>
  private static readonly TimeSpan DefaultMaxRecordingRangeWallSpan = TimeSpan.FromMinutes(1);

  public async Task<string> IngestAsync(ClipMeta clip, CancellationToken ct)
  {
    if (string.IsNullOrWhiteSpace(clip.CameraId) || string.IsNullOrWhiteSpace(clip.FilePath) || clip.StartTs is null)
      throw new ArgumentException("camera_id, file_path and start_ts are required");

    var clipUid = string.IsNullOrWhiteSpace(clip.ClipUid)
      ? Guid.NewGuid()
      : Guid.Parse(clip.ClipUid);

    await using var command = _dataSource.CreateCommand("""
      INSERT INTO clips (clip_uid, file_path, camera_id, start_ts, end_ts, size_bytes, retained, retained_at)
      VALUES ($1, $2, $3, $4, $5, $6, $7,
        CASE WHEN $7 THEN now() ELSE NULL END)
      ON CONFLICT (file_path) DO UPDATE SET
        camera_id = EXCLUDED.camera_id,
        start_ts = EXCLUDED.start_ts,
        end_ts = COALESCE(EXCLUDED.end_ts, clips.end_ts),
        size_bytes = GREATEST(clips.size_bytes, EXCLUDED.size_bytes),
        retained = clips.retained OR EXCLUDED.retained,
        retained_at = CASE WHEN clips.retained OR EXCLUDED.retained THEN COALESCE(clips.retained_at, now()) ELSE clips.retained_at END
      RETURNING clip_uid::text;
      """);

    command.Parameters.AddWithValue(NpgsqlDbType.Uuid, clipUid);
    command.Parameters.AddWithValue(NpgsqlDbType.Text, clip.FilePath);
    command.Parameters.AddWithValue(NpgsqlDbType.Text, clip.CameraId);
    command.Parameters.AddWithValue(NpgsqlDbType.TimestampTz, clip.StartTs.ToDateTime());
    command.Parameters.AddWithValue(
      NpgsqlDbType.TimestampTz,
      clip.EndTs is null ? DBNull.Value : clip.EndTs.ToDateTime());
    command.Parameters.AddWithValue(NpgsqlDbType.Bigint, (long)clip.SizeBytes);
    command.Parameters.AddWithValue(NpgsqlDbType.Boolean, clip.HasRetained ? clip.Retained : false);

    var stored = (string)(await command.ExecuteScalarAsync(ct))!;
    return stored;
  }

  public async Task RetainEventAsync(RetainEventRequest req, CancellationToken ct)
  {
    var eventId = string.IsNullOrWhiteSpace(req.EventId)
      ? $"{req.CameraId}-{req.AlarmStartTs.ToDateTime().ToUniversalTime().Ticks}"
      : req.EventId.Trim();

    var pre = req.PreRecordSeconds == 0 ? 30u : req.PreRecordSeconds;
    var post = req.PostRecordSeconds == 0 ? 30u : req.PostRecordSeconds;
    var timeout = req.TimeoutSeconds == 0 ? 300u : req.TimeoutSeconds;

    var alarmStart = req.AlarmStartTs.ToDateTime();
    if (alarmStart.Kind != DateTimeKind.Utc)
      alarmStart = alarmStart.ToUniversalTime();

    var retainFrom = alarmStart.AddSeconds(-pre);
    var retainUntil = alarmStart.AddSeconds(timeout);
    if (req.AlarmEndTs != null)
      retainUntil = req.AlarmEndTs.ToDateTime().ToUniversalTime().AddSeconds(post);

    if (retainUntil < retainFrom)
      throw new InvalidOperationException("retain_until_ts must be >= retain_from_ts");

    await using var connection = await _dataSource.OpenConnectionAsync(ct);
    await using var tx = await connection.BeginTransactionAsync(ct);

    await using (var upsert = new NpgsqlCommand("""
      INSERT INTO retention_events(event_id, camera_id, alarm_start_ts, alarm_end_ts, retain_from_ts, retain_until_ts, updated_at)
      VALUES($1, $2, $3, $4, $5, $6, now())
      ON CONFLICT(event_id) DO UPDATE SET
        camera_id = EXCLUDED.camera_id,
        alarm_start_ts = EXCLUDED.alarm_start_ts,
        alarm_end_ts = EXCLUDED.alarm_end_ts,
        retain_from_ts = EXCLUDED.retain_from_ts,
        retain_until_ts = EXCLUDED.retain_until_ts,
        updated_at = now();
      """, connection, tx))
    {
      upsert.Parameters.AddWithValue(eventId);
      upsert.Parameters.AddWithValue(req.CameraId);
      upsert.Parameters.AddWithValue(alarmStart);
      upsert.Parameters.AddWithValue(req.AlarmEndTs is null ? DBNull.Value : req.AlarmEndTs.ToDateTime());
      upsert.Parameters.AddWithValue(retainFrom);
      upsert.Parameters.AddWithValue(retainUntil);
      await upsert.ExecuteNonQueryAsync(ct);
    }

    await using (var retainClips = new NpgsqlCommand("""
      WITH updated AS (
        UPDATE clips AS c
        SET retained = TRUE, retained_at = COALESCE(c.retained_at, now())
        WHERE c.camera_id = $2
          AND c.start_ts <= $3
          AND COALESCE(c.end_ts, c.start_ts) >= $4
        RETURNING c.clip_uid
      )
      INSERT INTO retention_event_clips(event_id, clip_uid)
      SELECT $1, clip_uid FROM updated
      ON CONFLICT(event_id, clip_uid) DO NOTHING;
      """, connection, tx))
    {
      retainClips.Parameters.AddWithValue(eventId);
      retainClips.Parameters.AddWithValue(req.CameraId);
      retainClips.Parameters.AddWithValue(retainUntil);
      retainClips.Parameters.AddWithValue(retainFrom);
      await retainClips.ExecuteNonQueryAsync(ct);
    }

    await tx.CommitAsync(ct);
  }

  public async Task<List<Recording>> GetRecordingsFromToAsync(GetRecordingsFromToRequest request, CancellationToken ct)
  {
    var limit = request.Limit == 0 ? 100 : (int)request.Limit;
    await using var command = _dataSource.CreateCommand("""
      SELECT camera_id, file_path, start_ts, end_ts, size_bytes
      FROM clips
      WHERE camera_id = $1
        AND retained = TRUE
        AND ($2::timestamptz IS NULL OR COALESCE(end_ts, 'infinity'::timestamptz) >= $2)
        AND ($3::timestamptz IS NULL OR start_ts <= $3)
      ORDER BY start_ts DESC
      LIMIT $4;
      """);
    command.Parameters.AddWithValue(request.CameraId);
    command.Parameters.AddWithValue(
      NpgsqlDbType.TimestampTz,
      request.FromTs is null ? DBNull.Value : request.FromTs.ToDateTime());
    command.Parameters.AddWithValue(
      NpgsqlDbType.TimestampTz,
      request.ToTs is null ? DBNull.Value : request.ToTs.ToDateTime());
    command.Parameters.AddWithValue(limit);

    return await ReadRecordingListAsync(command, ct);
  }

  public async Task<List<Recording>> GetRecordingsAroundAsync(GetRecordingsAroundRequest request, CancellationToken ct)
  {
    var limit = request.Limit == 0 ? 100 : (int)request.Limit;
    var beforeLimit = limit / 2;
    var afterLimit = limit - beforeLimit;
    var center = request.Timestamp.ToDateTime();
    if (center.Kind != DateTimeKind.Utc)
      center = center.ToUniversalTime();

    var before = await QueryRecordingsAsync("""
      SELECT camera_id, file_path, start_ts, end_ts, size_bytes
      FROM clips
      WHERE camera_id = $1 AND retained = TRUE AND start_ts < $2
      ORDER BY start_ts DESC
      LIMIT $3;
      """, request.CameraId, center, beforeLimit, ct);

    var after = await QueryRecordingsAsync("""
      SELECT camera_id, file_path, start_ts, end_ts, size_bytes
      FROM clips
      WHERE camera_id = $1 AND retained = TRUE AND start_ts >= $2
      ORDER BY start_ts ASC
      LIMIT $3;
      """, request.CameraId, center, afterLimit, ct);

    var combined = new List<Recording>(before.Count + after.Count);
    for (var i = before.Count - 1; i >= 0; i--)
      combined.Add(before[i]);
    combined.AddRange(after);
    return combined;
  }

  public async Task<List<RecordingRange>> GetRecordingRangesAsync(GetRecordingRangesRequest request, CancellationToken ct)
  {
    var limit = request.Limit == 0 ? 100 : (int)request.Limit;
    var gapMs = request.GapToleranceMs <= 0 ? 1000 : (int)request.GapToleranceMs;
    var gap = TimeSpan.FromMilliseconds(gapMs);

    List<RecordingWithEvents> clips;
    var fetchLimit = Math.Max(limit * 10, limit);
    if (request.Timestamp != null)
    {
      var ts = request.Timestamp.ToDateTime();
      if (ts.Kind != DateTimeKind.Utc)
        ts = ts.ToUniversalTime();
      clips = await GetRecordingsAroundWithEventsAsync(request.CameraId, ts, fetchLimit, ct);
    }
    else
    {
      clips = await GetRecordingsWithEventsAsync(
        request.CameraId,
        request.FromTs,
        request.ToTs,
        fetchLimit,
        ct);
    }

    var ranges = GroupRecordingRanges(clips, gap, DefaultMaxRecordingRangeWallSpan);
    if (ranges.Count > limit)
      ranges = ranges.GetRange(0, limit);
    return ranges;
  }

  public async Task<(bool Found, RecordingRange? Range, Timestamp? ClipStartTs)> GetRecordingRangeForClipAsync(
    GetRecordingRangeForClipRequest request,
    CancellationToken ct)
  {
    DateTime? clipStart = null;
    await using (var cmd = _dataSource.CreateCommand("""
      SELECT start_ts FROM clips
      WHERE camera_id = $1 AND file_path = $2 AND retained = TRUE;
      """))
    {
      cmd.Parameters.AddWithValue(request.CameraId);
      cmd.Parameters.AddWithValue(request.FilePath);
      await using var reader = await cmd.ExecuteReaderAsync(ct);
      if (!await reader.ReadAsync(ct))
        return (false, null, null);
      clipStart = reader.GetDateTime(0).ToUniversalTime();
    }

    var gapMs = request.GapToleranceMs <= 0 ? 1000 : (int)request.GapToleranceMs;
    var ranges = await GetRecordingRangesAsync(new GetRecordingRangesRequest
    {
      CameraId = request.CameraId,
      Timestamp = Timestamp.FromDateTime(DateTime.SpecifyKind(clipStart!.Value, DateTimeKind.Utc)),
      Limit = 20,
      GapToleranceMs = (uint)gapMs
    }, ct);

    foreach (var r in ranges)
    {
      var start = r.StartTs.ToDateTime();
      var end = r.EndTs?.ToDateTime() ?? start;
      var tol = TimeSpan.FromMilliseconds(gapMs);
      if (clipStart.Value >= start && clipStart.Value <= end.Add(tol))
        return (true, r, Timestamp.FromDateTime(DateTime.SpecifyKind(clipStart.Value, DateTimeKind.Utc)));
    }

    return (false, null, Timestamp.FromDateTime(DateTime.SpecifyKind(clipStart.Value, DateTimeKind.Utc)));
  }

  public async Task<(bool Found, string FilePath, uint PositionMs)> ResolveRangePositionAsync(
    ResolveRangePositionRequest request,
    CancellationToken ct)
  {
    var gapMs = request.GapToleranceMs <= 0 ? 1000 : (int)request.GapToleranceMs;
    var rangeStart = request.RangeStartTs.ToDateTime();
    if (rangeStart.Kind != DateTimeKind.Utc)
      rangeStart = rangeStart.ToUniversalTime();

    var ranges = await GetRecordingRangesAsync(new GetRecordingRangesRequest
    {
      CameraId = request.CameraId,
      Timestamp = Timestamp.FromDateTime(rangeStart),
      Limit = 20,
      GapToleranceMs = (uint)gapMs
    }, ct);

    RecordingRange? target = null;
    foreach (var r in ranges)
    {
      if (r.StartTs.ToDateTime().Equals(rangeStart))
      {
        target = r;
        break;
      }
    }

    if (target is null)
      return (false, string.Empty, 0);

    var targetTs = target.StartTs.ToDateTime().AddMilliseconds(request.RangePositionMs);

    await using var cmd = _dataSource.CreateCommand("""
      SELECT file_path, start_ts
      FROM clips
      WHERE camera_id = $1 AND retained = TRUE
        AND start_ts <= $2 AND COALESCE(end_ts, start_ts) >= $2
      ORDER BY start_ts DESC
      LIMIT 1;
      """);
    cmd.Parameters.AddWithValue(request.CameraId);
    cmd.Parameters.AddWithValue(targetTs);
    await using var reader = await cmd.ExecuteReaderAsync(ct);
    if (!await reader.ReadAsync(ct))
      return (false, string.Empty, 0);

    var fp = reader.GetString(0);
    var clipStart = reader.GetDateTime(1).ToUniversalTime();
    var pos = targetTs - clipStart;
    if (pos < TimeSpan.Zero)
      pos = TimeSpan.Zero;
    return (true, fp, (uint)pos.TotalMilliseconds);
  }

  public async Task<List<Recording>> GetNextRecordingAfterAsync(GetNextRecordingAfterRequest request, CancellationToken ct)
  {
    await using var command = _dataSource.CreateCommand("""
      SELECT c.camera_id, c.file_path, c.start_ts, c.end_ts, c.size_bytes
      FROM clips c
      INNER JOIN clips cur ON cur.camera_id = $1 AND cur.file_path = $2 AND cur.retained = TRUE
      WHERE c.camera_id = $1 AND c.retained = TRUE AND c.start_ts > cur.start_ts
      ORDER BY c.start_ts ASC
      LIMIT 1;
      """);
    command.Parameters.AddWithValue(request.CameraId);
    command.Parameters.AddWithValue(request.FilePath);

    var list = await ReadRecordingListAsync(command, ct);
    return list;
  }

  public async Task<bool> HasActiveRetentionWindowAsync(string cameraId, CancellationToken ct)
  {
    await using var cmd = _dataSource.CreateCommand("""
      SELECT EXISTS (
        SELECT 1 FROM retention_events
        WHERE camera_id = $1 AND retain_from_ts <= now() AND retain_until_ts >= now()
      );
      """);
    cmd.Parameters.AddWithValue(cameraId);
    return (bool)(await cmd.ExecuteScalarAsync(ct))!;
  }

  public async Task<(List<RetentionEventRow> Events, bool HasMore, Timestamp? NextBefore)> ListRetentionEventsAsync(
    uint limit,
    Timestamp? beforeAlarmStart,
    CancellationToken ct)
  {
    var lim = limit == 0 || limit > 200 ? 50 : (int)limit;

    await using var cmd = beforeAlarmStart is null
      ? _dataSource.CreateCommand("""
        SELECT event_id, camera_id, alarm_start_ts, alarm_end_ts
        FROM retention_events
        ORDER BY alarm_start_ts DESC, event_id DESC
        LIMIT $1;
        """)
      : _dataSource.CreateCommand("""
        SELECT event_id, camera_id, alarm_start_ts, alarm_end_ts
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
        EventId = reader.GetString(0),
        CameraId = reader.GetString(1),
        AlarmStartTs = Timestamp.FromDateTime(reader.GetDateTime(2).ToUniversalTime())
      };
      if (!reader.IsDBNull(3))
        row.AlarmEndTs = Timestamp.FromDateTime(reader.GetDateTime(3).ToUniversalTime());
      events.Add(row);
    }

    var hasMore = events.Count == lim && lim > 0;
    Timestamp? nextBefore = null;
    if (hasMore && events.Count > 0)
      nextBefore = events[^1].AlarmStartTs;

    return (events, hasMore, nextBefore);
  }

  private async Task<List<Recording>> QueryRecordingsAsync(
    string sql,
    string cameraId,
    DateTime ts,
    int lim,
    CancellationToken ct)
  {
    await using var command = _dataSource.CreateCommand(sql);
    command.Parameters.AddWithValue(cameraId);
    command.Parameters.AddWithValue(ts);
    command.Parameters.AddWithValue(lim);
    return await ReadRecordingListAsync(command, ct);
  }

  private static async Task<List<Recording>> ReadRecordingListAsync(NpgsqlCommand command, CancellationToken ct)
  {
    var recordings = new List<Recording>();
    await using var reader = await command.ExecuteReaderAsync(ct);
    while (await reader.ReadAsync(ct))
    {
      recordings.Add(ReadRecording(reader));
    }

    return recordings;
  }

  private static Recording ReadRecording(NpgsqlDataReader reader)
  {
    var rec = new Recording
    {
      CameraId = reader.GetString(0),
      FilePath = reader.GetString(1),
      StartTs = Timestamp.FromDateTime(reader.GetDateTime(2).ToUniversalTime()),
      SizeBytes = (ulong)reader.GetInt64(4)
    };
    if (!reader.IsDBNull(3))
      rec.EndTs = Timestamp.FromDateTime(reader.GetDateTime(3).ToUniversalTime());
    return rec;
  }

  private async Task<List<RecordingWithEvents>> GetRecordingsWithEventsAsync(
    string cameraId,
    Timestamp? fromTs,
    Timestamp? toTs,
    int limit,
    CancellationToken ct)
  {
    await using var command = _dataSource.CreateCommand("""
      SELECT c.camera_id, c.file_path, c.start_ts, c.end_ts, c.size_bytes,
             COALESCE(string_agg(DISTINCT rec.event_id, ','), '')
      FROM clips c
      LEFT JOIN retention_event_clips rec ON rec.clip_uid = c.clip_uid
      WHERE c.camera_id = $1 AND c.retained = TRUE
        AND ($2::timestamptz IS NULL OR COALESCE(c.end_ts, 'infinity'::timestamptz) >= $2)
        AND ($3::timestamptz IS NULL OR c.start_ts <= $3)
      GROUP BY c.clip_uid
      ORDER BY c.start_ts ASC
      LIMIT $4;
      """);
    command.Parameters.AddWithValue(cameraId);
    command.Parameters.AddWithValue(
      NpgsqlDbType.TimestampTz,
      fromTs is null ? DBNull.Value : fromTs.ToDateTime());
    command.Parameters.AddWithValue(
      NpgsqlDbType.TimestampTz,
      toTs is null ? DBNull.Value : toTs.ToDateTime());
    command.Parameters.AddWithValue(limit);
    return await ReadRecordingWithEventsAsync(command, ct);
  }

  private async Task<List<RecordingWithEvents>> GetRecordingsAroundWithEventsAsync(
    string cameraId,
    DateTime center,
    int limit,
    CancellationToken ct)
  {
    var beforeLimit = limit / 2;
    var afterLimit = limit - beforeLimit;

    var before = await QueryRecordingsWithEventsAsync("""
      SELECT c.camera_id, c.file_path, c.start_ts, c.end_ts, c.size_bytes,
             COALESCE(string_agg(DISTINCT rec.event_id, ','), '')
      FROM clips c
      LEFT JOIN retention_event_clips rec ON rec.clip_uid = c.clip_uid
      WHERE c.camera_id = $1 AND c.retained = TRUE AND c.start_ts < $2
      GROUP BY c.clip_uid
      ORDER BY c.start_ts DESC
      LIMIT $3;
      """, cameraId, center, beforeLimit, ct);

    var after = await QueryRecordingsWithEventsAsync("""
      SELECT c.camera_id, c.file_path, c.start_ts, c.end_ts, c.size_bytes,
             COALESCE(string_agg(DISTINCT rec.event_id, ','), '')
      FROM clips c
      LEFT JOIN retention_event_clips rec ON rec.clip_uid = c.clip_uid
      WHERE c.camera_id = $1 AND c.retained = TRUE AND c.start_ts >= $2
      GROUP BY c.clip_uid
      ORDER BY c.start_ts ASC
      LIMIT $3;
      """, cameraId, center, afterLimit, ct);

    var combined = new List<RecordingWithEvents>(before.Count + after.Count);
    for (var i = before.Count - 1; i >= 0; i--)
      combined.Add(before[i]);
    combined.AddRange(after);
    return combined;
  }

  private async Task<List<RecordingWithEvents>> QueryRecordingsWithEventsAsync(
    string sql,
    string cameraId,
    DateTime ts,
    int lim,
    CancellationToken ct)
  {
    await using var command = _dataSource.CreateCommand(sql);
    command.Parameters.AddWithValue(cameraId);
    command.Parameters.AddWithValue(ts);
    command.Parameters.AddWithValue(lim);
    return await ReadRecordingWithEventsAsync(command, ct);
  }

  private static async Task<List<RecordingWithEvents>> ReadRecordingWithEventsAsync(NpgsqlCommand command, CancellationToken ct)
  {
    var list = new List<RecordingWithEvents>();
    await using var reader = await command.ExecuteReaderAsync(ct);
    while (await reader.ReadAsync(ct))
    {
      var rec = ReadRecording(reader);
      var ev = reader.IsDBNull(5) ? "" : reader.GetString(5);
      var ids = string.IsNullOrEmpty(ev)
        ? new List<string>()
        : ev.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct().ToList();
      list.Add(new RecordingWithEvents(rec, ids));
    }

    return list;
  }

  /// <summary>
  /// Builds playback ranges: merge clips while wall-clock pause from previous clip end to this clip start
  /// is ≤ <paramref name="gapTolerance"/> (recording not interrupted). Chains longer than
  /// <paramref name="maxRangeWallSpan"/> are split on clip boundaries so the UI stays minute-grained.
  /// If the previous clip has no end_ts beyond start_ts, merge only while consecutive starts are within
  /// 65 seconds (rolling segments without reliable ends).
  /// </summary>
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

    void PushRange(RecordingWithEvents it)
    {
      var c = it.Recording;
      ranges.Add(new RecordingRange
      {
        CameraId = c.CameraId,
        FilePath = c.FilePath,
        StartTs = c.StartTs,
        EndTs = c.EndTs ?? c.StartTs,
        ClipsCount = 1,
      });
      foreach (var id in it.EventIds)
      {
        if (!string.IsNullOrEmpty(id))
          ranges[^1].EventIds.Add(id);
      }
    }

    foreach (var item in clips)
    {
      var clip = item.Recording;
      var start = AsUtc(clip.StartTs.ToDateTime());
      var endRaw = clip.EndTs?.ToDateTime();
      var end = endRaw.HasValue && AsUtc(endRaw.Value) > start ? AsUtc(endRaw.Value) : start;

      if (ranges.Count == 0)
      {
        PushRange(item);
        prevItem = item;
        continue;
      }

      var cur = ranges[^1];
      var rangeFirstStart = AsUtc(cur.StartTs.ToDateTime());
      var spanExceeded =
        maxRangeWallSpan > TimeSpan.Zero && start - rangeFirstStart >= maxRangeWallSpan;

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
        PushRange(item);
      else
      {
        var rangeEnd = AsUtc(cur.EndTs!.ToDateTime());
        if (end > rangeEnd)
          cur.EndTs = Timestamp.FromDateTime(DateTime.SpecifyKind(end, DateTimeKind.Utc));
        cur.ClipsCount += 1;
        foreach (var id in item.EventIds)
        {
          if (!string.IsNullOrEmpty(id) && !cur.EventIds.Contains(id))
            cur.EventIds.Add(id);
        }
      }

      prevItem = item;
    }

    return ranges;
  }
}
