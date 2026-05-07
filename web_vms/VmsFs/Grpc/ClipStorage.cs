using Google.Protobuf.WellKnownTypes;
using Npgsql;
using NpgsqlTypes;
using Vms.Fs;

namespace VmsFs;

internal sealed record RecordingWithEvents(
  Recording Recording,
  List<string> EventIds);

public sealed class ClipStorage(NpgsqlDataSource dataSource, CameraConfigCache configCache)
{
  private readonly NpgsqlDataSource _dataSource = dataSource;
  private readonly CameraConfigCache _configCache = configCache;

  /// <summary>Split uninterrupted spans into ~minute-sized ranges for the archive list.</summary>
  private static readonly TimeSpan DefaultMaxRecordingRangeWallSpan = TimeSpan.FromMinutes(1);

  public async Task<string> IngestAsync(Recording clip, CancellationToken ct)
  {
    if (string.IsNullOrWhiteSpace(clip.CameraId) || string.IsNullOrWhiteSpace(clip.FilePath) || clip.StartTs is null)
      throw new ArgumentException("camera_id, file_path and start_ts are required");

    var clipUid = string.IsNullOrWhiteSpace(clip.ClipUid)
      ? Guid.NewGuid()
      : Guid.Parse(clip.ClipUid);

    var config = await _configCache.GetAsync(clip.CameraId, ct);
    var retained = await ResolveRetainedAsync(clip, config, ct);

    await using var command = _dataSource.CreateCommand("""
      INSERT INTO clips (clip_uid, file_path, camera_id, start_ts, end_ts, size_bytes, retained)
      VALUES ($1, $2, $3, $4, $5, $6, $7)
      ON CONFLICT (file_path) DO UPDATE SET
        camera_id = EXCLUDED.camera_id,
        start_ts = EXCLUDED.start_ts,
        end_ts = COALESCE(EXCLUDED.end_ts, clips.end_ts),
        size_bytes = GREATEST(clips.size_bytes, EXCLUDED.size_bytes),
        retained = clips.retained OR EXCLUDED.retained
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
    command.Parameters.AddWithValue(NpgsqlDbType.Boolean, retained);

    var stored = (string)(await command.ExecuteScalarAsync(ct))!;
    return stored;
  }

  public async Task RetainEventAsync(RetainEventRequest req, CancellationToken ct)
  {
    var eventId = string.IsNullOrWhiteSpace(req.EventId)
      ? $"{req.CameraId}-{req.AlarmStartTs.ToDateTime().ToUniversalTime().Ticks}"
      : req.EventId.Trim();

    var alarmStart = req.AlarmStartTs.ToDateTime();
    if (alarmStart.Kind != DateTimeKind.Utc)
      alarmStart = alarmStart.ToUniversalTime();

    var timeout = req.TimeoutSeconds == 0 ? 30u : req.TimeoutSeconds;
    var alarmEnd = req.AlarmEndTs is not null
      ? req.AlarmEndTs.ToDateTime().ToUniversalTime()
      : alarmStart.AddSeconds(timeout);

    var config = await _configCache.GetAsync(req.CameraId, ct);
    var preSeconds = config?.PreRecordSeconds ?? 30;
    var postSeconds = config?.PostRecordSeconds ?? 30;

    await using var upsert = _dataSource.CreateCommand("""
      INSERT INTO retention_events(event_id, camera_id, alarm_start_ts, alarm_end_ts)
      VALUES($1, $2, $3, COALESCE($4::timestamptz, $3 + ($5 * interval '1 second')))
      ON CONFLICT(event_id) DO UPDATE SET
        camera_id = EXCLUDED.camera_id,
        alarm_start_ts = EXCLUDED.alarm_start_ts,
        alarm_end_ts = COALESCE($4::timestamptz, retention_events.alarm_end_ts);
      """);
    upsert.Parameters.AddWithValue(eventId);
    upsert.Parameters.AddWithValue(req.CameraId);
    upsert.Parameters.AddWithValue(alarmStart);
    upsert.Parameters.AddWithValue(req.AlarmEndTs is null ? DBNull.Value : req.AlarmEndTs.ToDateTime());
    upsert.Parameters.AddWithValue((int)timeout);
    await upsert.ExecuteNonQueryAsync(ct);

    await using var retainClips = _dataSource.CreateCommand("""
      UPDATE clips
      SET retained = TRUE
      WHERE camera_id = $1
        AND start_ts <= $2 + ($5 * interval '1 second')
        AND COALESCE(end_ts, start_ts) >= $3 - ($4 * interval '1 second');
      """);
    retainClips.Parameters.AddWithValue(req.CameraId);
    retainClips.Parameters.AddWithValue(alarmEnd);
    retainClips.Parameters.AddWithValue(alarmStart);
    retainClips.Parameters.AddWithValue(preSeconds);
    retainClips.Parameters.AddWithValue(postSeconds);
    await retainClips.ExecuteNonQueryAsync(ct);
  }

  public async Task<List<Recording>> GetRecordingsFromToAsync(GetRecordingsFromToRequest request, CancellationToken ct)
  {
    var limit = request.Limit == 0 ? 100 : (int)request.Limit;
    await using var command = _dataSource.CreateCommand("""
      SELECT camera_id, file_path, start_ts, end_ts, size_bytes, clip_uid::text
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
      SELECT camera_id, file_path, start_ts, end_ts, size_bytes, clip_uid::text
      FROM clips
      WHERE camera_id = $1 AND retained = TRUE AND start_ts < $2
      ORDER BY start_ts DESC
      LIMIT $3;
      """, request.CameraId, center, beforeLimit, ct);

    var after = await QueryRecordingsAsync("""
      SELECT camera_id, file_path, start_ts, end_ts, size_bytes, clip_uid::text
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
      if (r.Recordings.Count > 0 && r.Recordings[0].StartTs.ToDateTime().Equals(rangeStart))
      {
        target = r;
        break;
      }
    }

    if (target is null)
      return (false, string.Empty, 0);

    var targetTs = target.Recordings[0].StartTs.ToDateTime().AddMilliseconds(request.RangePositionMs);

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
      SELECT c.camera_id, c.file_path, c.start_ts, c.end_ts, c.size_bytes, c.clip_uid::text
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

  public async Task<List<Recording>> GetClipsFromAsync(GetClipsFromRequest request, CancellationToken ct)
  {
    var count = request.Count == 0 ? 1 : (int)request.Count;
    await using var command = _dataSource.CreateCommand("""
      SELECT c.camera_id, c.file_path, c.start_ts, c.end_ts, c.size_bytes, c.clip_uid::text
      FROM clips c
      INNER JOIN clips anchor ON anchor.clip_uid = $2 AND anchor.camera_id = $1
      WHERE c.camera_id = $1 AND c.retained = TRUE AND c.start_ts >= anchor.start_ts
      ORDER BY c.start_ts ASC
      LIMIT $3;
      """);
    command.Parameters.AddWithValue(request.CameraId);
    command.Parameters.AddWithValue(NpgsqlDbType.Uuid, Guid.Parse(request.ClipUid));
    command.Parameters.AddWithValue(count);

    return await ReadRecordingListAsync(command, ct);
  }

  public async Task<List<(Guid ClipUid, string FilePath, string CameraId, DateTime EndTs)>> GetUnretainedCandidatesAsync(
    int minOlderThanSeconds,
    int batchSize,
    CancellationToken ct)
  {
    await using var cmd = _dataSource.CreateCommand("""
      SELECT clip_uid, file_path, camera_id, end_ts
      FROM clips
      WHERE retained = false
        AND end_ts IS NOT NULL
        AND end_ts < now() - ($1 * interval '1 second')
      ORDER BY start_ts ASC
      LIMIT $2;
      """);
    cmd.Parameters.AddWithValue(minOlderThanSeconds);
    cmd.Parameters.AddWithValue(batchSize);

    var result = new List<(Guid, string, string, DateTime)>();
    await using var reader = await cmd.ExecuteReaderAsync(ct);
    while (await reader.ReadAsync(ct))
    {
      result.Add((
        reader.GetGuid(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetDateTime(3).ToUniversalTime()
      ));
    }
    return result;
  }

  public async Task DeleteClipRecordAsync(Guid clipUid, CancellationToken ct)
  {
    await using var cmd = _dataSource.CreateCommand("""
      DELETE FROM clips WHERE clip_uid = $1;
      """);
    cmd.Parameters.AddWithValue(NpgsqlDbType.Uuid, clipUid);
    await cmd.ExecuteNonQueryAsync(ct);
  }

  public async Task<bool> HasActiveRetentionWindowAsync(string cameraId, CancellationToken ct)
  {
    await using var cmd = _dataSource.CreateCommand("""
      SELECT EXISTS (
        SELECT 1 FROM retention_events
        WHERE camera_id = $1 AND alarm_start_ts <= now() AND alarm_end_ts >= now()
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
      SizeBytes = (ulong)reader.GetInt64(4),
      ClipUid = reader.IsDBNull(5) ? "" : reader.GetString(5)
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
    var config = await _configCache.GetAsync(cameraId, ct);
    var pre = config?.PreRecordSeconds ?? 30;
    var post = config?.PostRecordSeconds ?? 30;

    await using var command = _dataSource.CreateCommand("""
      SELECT c.camera_id, c.file_path, c.start_ts, c.end_ts, c.size_bytes, c.clip_uid::text,
             COALESCE(string_agg(DISTINCT re.event_id, ','), '')
      FROM clips c
      LEFT JOIN retention_events re ON re.camera_id = c.camera_id
        AND c.start_ts <= re.alarm_end_ts + ($5 * interval '1 second')
        AND COALESCE(c.end_ts, c.start_ts) >= re.alarm_start_ts - ($6 * interval '1 second')
      WHERE c.camera_id = $1 AND c.retained = TRUE
        AND ($2::timestamptz IS NULL OR COALESCE(c.end_ts, 'infinity'::timestamptz) >= $2)
        AND ($3::timestamptz IS NULL OR c.start_ts <= $3)
      GROUP BY c.clip_uid
      ORDER BY c.start_ts ASC
      LIMIT $4;
      """);
    command.Parameters.AddWithValue(cameraId);
    command.Parameters.AddWithValue(NpgsqlDbType.TimestampTz, fromTs is null ? DBNull.Value : fromTs.ToDateTime());
    command.Parameters.AddWithValue(NpgsqlDbType.TimestampTz, toTs is null ? DBNull.Value : toTs.ToDateTime());
    command.Parameters.AddWithValue(limit);
    command.Parameters.AddWithValue(post);
    command.Parameters.AddWithValue(pre);
    return await ReadRecordingWithEventsAsync(command, ct);
  }

  private async Task<List<RecordingWithEvents>> GetRecordingsAroundWithEventsAsync(
    string cameraId,
    DateTime center,
    int limit,
    CancellationToken ct)
  {
    var config = await _configCache.GetAsync(cameraId, ct);
    var pre = config?.PreRecordSeconds ?? 30;
    var post = config?.PostRecordSeconds ?? 30;

    var beforeLimit = limit / 2;
    var afterLimit = limit - beforeLimit;

    var before = await QueryRecordingsWithEventsAsync("""
      SELECT c.camera_id, c.file_path, c.start_ts, c.end_ts, c.size_bytes, c.clip_uid::text,
             COALESCE(string_agg(DISTINCT re.event_id, ','), '')
      FROM clips c
      LEFT JOIN retention_events re ON re.camera_id = c.camera_id
        AND c.start_ts <= re.alarm_end_ts + ($4 * interval '1 second')
        AND COALESCE(c.end_ts, c.start_ts) >= re.alarm_start_ts - ($5 * interval '1 second')
      WHERE c.camera_id = $1 AND c.retained = TRUE AND c.start_ts < $2
      GROUP BY c.clip_uid
      ORDER BY c.start_ts DESC
      LIMIT $3;
      """, cameraId, center, beforeLimit, post, pre, ct);

    var after = await QueryRecordingsWithEventsAsync("""
      SELECT c.camera_id, c.file_path, c.start_ts, c.end_ts, c.size_bytes, c.clip_uid::text,
             COALESCE(string_agg(DISTINCT re.event_id, ','), '')
      FROM clips c
      LEFT JOIN retention_events re ON re.camera_id = c.camera_id
        AND c.start_ts <= re.alarm_end_ts + ($4 * interval '1 second')
        AND COALESCE(c.end_ts, c.start_ts) >= re.alarm_start_ts - ($5 * interval '1 second')
      WHERE c.camera_id = $1 AND c.retained = TRUE AND c.start_ts >= $2
      GROUP BY c.clip_uid
      ORDER BY c.start_ts ASC
      LIMIT $3;
      """, cameraId, center, afterLimit, post, pre, ct);

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
    int post,
    int pre,
    CancellationToken ct)
  {
    await using var command = _dataSource.CreateCommand(sql);
    command.Parameters.AddWithValue(cameraId);
    command.Parameters.AddWithValue(ts);
    command.Parameters.AddWithValue(lim);
    command.Parameters.AddWithValue(post);
    command.Parameters.AddWithValue(pre);
    return await ReadRecordingWithEventsAsync(command, ct);
  }

  private static async Task<List<RecordingWithEvents>> ReadRecordingWithEventsAsync(NpgsqlCommand command, CancellationToken ct)
  {
    var list = new List<RecordingWithEvents>();
    await using var reader = await command.ExecuteReaderAsync(ct);
    while (await reader.ReadAsync(ct))
    {
      var rec = ReadRecording(reader);
      var ev = reader.IsDBNull(6) ? "" : reader.GetString(6);
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

    if (config.RecordingMode != Vms.Cfg.RecordingMode.RecordingPrealarm)
      return false;

    var startTs = clip.StartTs.ToDateTime();
    var endTs = clip.EndTs is not null ? clip.EndTs.ToDateTime() : (DateTime?)null;

    await using var cmd = _dataSource.CreateCommand("""
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
    cmd.Parameters.AddWithValue(config.PostRecordSeconds);
    cmd.Parameters.AddWithValue(config.PreRecordSeconds);
    return (bool)(await cmd.ExecuteScalarAsync(ct))!;
  }
}
