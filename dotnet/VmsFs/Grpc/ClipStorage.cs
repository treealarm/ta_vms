using Google.Protobuf.WellKnownTypes;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using Vms.Fs;

namespace VmsFs;

internal sealed record RecordingWithEvents(
  Recording Recording,
  List<string> EventIds);

/// <summary>A clip a dead run left open, and the moment the archive picked up again.</summary>
public sealed record AbandonedClip(
  string CameraId,
  string StreamName,
  DateTime StartTs,
  /// <summary>When the archive picked up again, or null while it still has not.</summary>
  DateTime? ResumedAt);

public sealed class ClipStorage(
  IDbContextFactory<ClipsDbContext> dbFactory,
  NpgsqlDataSource dataSource,
  CameraConfigCache configCache,
  StorageListCache storageListCache,
  FsActivityLog activityLog)
{
  /// <summary>
  /// How recently a clip's file must have been touched to count as still being written. Longer than
  /// the liveness heartbeat, so a live writer is never mistaken for a dead one during a quiet moment.
  /// </summary>
  private static readonly TimeSpan StillBeingWritten = TimeSpan.FromSeconds(25);

  // Cap on how long one contiguous archive range (a single HLS source) may span. Uncapped, an
  // uninterrupted multi-hour recording becomes one HLS source with thousands of segments, and
  // building that manifest reads a sidx per clip — it grows to tens of seconds and trips the
  // player's manifestLoadTimeout. Capping the span bounds each source (the player rebuilds at each
  // boundary — a brief re-buffer every few minutes, the accepted trade for playable archive).
  private static readonly TimeSpan DefaultMaxRecordingRangeWallSpan = TimeSpan.FromMinutes(3);

  // Safety-net cap on how long a single retention episode can keep extending, for callers that
  // reuse one event_id indefinitely (ONVIF / manual REST). Analytics is already bounded upstream by
  // VmsAnalyticsSingleton minting a fresh event_id every few minutes, but a runaway event_id would otherwise
  // grow one episode's clip list — and its HLS manifest — without bound. Past this, RetainEvent
  // rolls over into a new episode (new event_uuid, same event_id group).
  private static readonly TimeSpan MaxEpisodeDuration = TimeSpan.FromMinutes(3);

  public async Task<string> IngestAsync(Recording clip, CancellationToken ct, bool forceRetained = false)
  {
    if (string.IsNullOrWhiteSpace(clip.CameraId) || string.IsNullOrWhiteSpace(clip.FilePath) || clip.StartTs is null)
      throw new ArgumentException("camera_id, file_path and start_ts are required");

    var clipUid = string.IsNullOrWhiteSpace(clip.ClipUid) ? Guid.NewGuid() : Guid.Parse(clip.ClipUid);
    // Edge import bypasses the policy: an operator asked for this exact range, so retention rules
    // derived from the camera's recording mode do not apply to it.
    var retained = forceRetained;
    if (!forceRetained)
    {
      var config = await configCache.GetAsync(clip.CameraId, ct, clip.StreamName);
      retained = await ResolveRetainedAsync(clip, config, ct);
    }
    var startTs = clip.StartTs.ToDateTime().ToUniversalTime();
    var endTs = clip.EndTs?.ToDateTime().ToUniversalTime();

    await using var cmd = dataSource.CreateCommand("""
      INSERT INTO clips (clip_uid, file_path, camera_id, stream_name, storage_id, start_ts, end_ts, size_bytes, retained, origin)
      VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10)
      ON CONFLICT (file_path) DO UPDATE SET
        camera_id   = EXCLUDED.camera_id,
        stream_name = EXCLUDED.stream_name,
        storage_id  = EXCLUDED.storage_id,
        start_ts    = EXCLUDED.start_ts,
        end_ts      = COALESCE(EXCLUDED.end_ts, clips.end_ts),
        -- GREATEST only while the clip is still being written: live ingest announces it at open
        -- with size 0 and again at close with the real size. A finalized ingest for a path that
        -- already exists means the file was rewritten, and keeping the larger of the two sizes
        -- would describe a file that is no longer there.
        size_bytes  = CASE WHEN EXCLUDED.end_ts IS NOT NULL
                        THEN EXCLUDED.size_bytes
                        ELSE GREATEST(clips.size_bytes, EXCLUDED.size_bytes) END,
        -- Same reason: an index parsed from the previous file points past the end of the new one,
        -- and every playback of it fails on the first fragment. Dropped here and rebuilt lazily or
        -- by the warmer.
        frag_index  = CASE WHEN EXCLUDED.end_ts IS NOT NULL THEN NULL ELSE clips.frag_index END,
        retained    = clips.retained OR EXCLUDED.retained,
        origin      = EXCLUDED.origin
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
    cmd.Parameters.AddWithValue(NpgsqlDbType.Text,
      string.IsNullOrWhiteSpace(clip.Origin) ? "live" : clip.Origin);

    var returnedUid = (string)(await cmd.ExecuteScalarAsync(ct))!;
    activityLog.Record(new FsActivityEntry(
      Guid.TryParse(returnedUid, out var parsedUid) ? parsedUid : clipUid,
      clip.FilePath, clip.CameraId, clip.StreamName ?? "",
      retained, (long)clip.SizeBytes, DateTime.UtcNow, "ingested", endTs));
    return returnedUid;
  }

  // Opens or extends a retention episode for (event_id, camera_id) and returns the episode's
  // event_uuid plus the alarm-override-until timestamp (alarm_end + post_record_seconds). While the
  // latest episode for this (event_id, camera_id) is still open it is extended (same event_uuid);
  // once it has expired, a fresh episode (new event_uuid) is opened under the same event_id group.
  public async Task<(Guid EventUuid, DateTime OverrideUntil)> RetainEventAsync(RetainEventRequest req, CancellationToken ct)
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
    var trigger = req.HasTrigger && !string.IsNullOrWhiteSpace(req.Trigger)
      ? req.Trigger
      : null;
    var objectId = req.HasObjectId && !string.IsNullOrWhiteSpace(req.ObjectId)
      ? req.ObjectId : null;

    var config = await configCache.GetAsync(req.CameraId, ct);
    var preSeconds  = config?.PreRecordSeconds  ?? 30;
    var postSeconds = config?.PostRecordSeconds ?? 30;

    await using var conn = await dataSource.OpenConnectionAsync(ct);
    await using var tx = await conn.BeginTransactionAsync(ct);

    // Serialize concurrent RetainEvent for the same (event_id, camera_id). A plain SELECT ... FOR
    // UPDATE below only locks an EXISTING episode row — for the very first call (no row yet) two
    // concurrent transactions would both find nothing and both INSERT a duplicate episode. A
    // transaction-scoped advisory lock covers that gap; it auto-releases on commit/rollback.
    await using (var lockCmd = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtext($1))", conn, tx))
    {
      lockCmd.Parameters.AddWithValue($"{eventId}|{req.CameraId}");
      await lockCmd.ExecuteNonQueryAsync(ct);
    }

    // Find the latest episode for this (event_id, camera_id). If it is still open, extend it;
    // otherwise open a new one.
    Guid? openUuid = null;
    await using (var findCmd = new NpgsqlCommand("""
      SELECT event_uuid, alarm_end_ts, alarm_start_ts
      FROM retention_events
      WHERE event_id = $1 AND camera_id = $2
      ORDER BY alarm_start_ts DESC
      LIMIT 1
      """, conn, tx))
    {
      findCmd.Parameters.AddWithValue(eventId);
      findCmd.Parameters.AddWithValue(req.CameraId);
      await using var reader = await findCmd.ExecuteReaderAsync(ct);
      if (await reader.ReadAsync(ct))
      {
        var openEnd   = reader.GetDateTime(1).ToUniversalTime();
        var openStart = reader.GetDateTime(2).ToUniversalTime();
        // Extend only while the episode is still open AND under the duration cap; past the cap, fall
        // through to open a fresh episode (same event_id group) so no single episode grows unbounded.
        if (openEnd > DateTime.UtcNow && DateTime.UtcNow - openStart < MaxEpisodeDuration)
          openUuid = reader.GetGuid(0);
      }
    }

    Guid eventUuid;
    if (openUuid is Guid existing)
    {
      eventUuid = existing;
      await using var ext = new NpgsqlCommand("""
        UPDATE retention_events
        SET alarm_end_ts = GREATEST(alarm_end_ts, $2),
            object_id = COALESCE($3, object_id),
            -- COALESCE keeps whatever opened the episode: a renewal that omits the cause must not
            -- erase it, and the first trigger is the one that describes why the episode exists.
            trigger = COALESCE(trigger, $4)
        WHERE event_uuid = $1;
        """, conn, tx);
      ext.Parameters.AddWithValue(eventUuid);
      ext.Parameters.AddWithValue(alarmEnd);
      ext.Parameters.AddWithValue((object?)objectId ?? DBNull.Value);
      ext.Parameters.AddWithValue((object?)trigger ?? DBNull.Value);
      await ext.ExecuteNonQueryAsync(ct);
    }
    else
    {
      eventUuid = Guid.NewGuid();
      await using var ins = new NpgsqlCommand("""
        INSERT INTO retention_events(event_uuid, event_id, camera_id, stream_name, alarm_start_ts, alarm_end_ts, object_id, trigger)
        VALUES($1, $2, $3, $4, $5, $6, $7, $8);
        """, conn, tx);
      ins.Parameters.AddWithValue(eventUuid);
      ins.Parameters.AddWithValue(eventId);
      ins.Parameters.AddWithValue(req.CameraId);
      ins.Parameters.AddWithValue((object?)streamName ?? DBNull.Value);
      ins.Parameters.AddWithValue(alarmStart);
      ins.Parameters.AddWithValue(alarmEnd);
      ins.Parameters.AddWithValue((object?)objectId ?? DBNull.Value);
      ins.Parameters.AddWithValue((object?)trigger ?? DBNull.Value);
      await ins.ExecuteNonQueryAsync(ct);
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
    return (eventUuid, alarmEnd.AddSeconds(postSeconds));
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

  public async Task<List<string>> GetRecordingCoverageAsync(GetRecordingCoverageRequest request, CancellationToken ct)
  {
    await using var db = await dbFactory.CreateDbContextAsync(ct);
    var q = db.Clips.Where(c => c.CameraId == request.CameraId && c.Retained);

    if (request.FromTs != null)
      q = q.Where(c => c.StartTs >= request.FromTs.ToDateTime().ToUniversalTime());
    if (request.ToTs != null)
      q = q.Where(c => c.StartTs < request.ToTs.ToDateTime().ToUniversalTime());

    switch (request.Granularity)
    {
      case RecordingCoverageGranularity.RecordingCoverageMonth:
        var months = await q.Select(c => new { c.StartTs.Year, c.StartTs.Month }).Distinct().ToListAsync(ct);
        return months.Select(m => $"{m.Year:D4}-{m.Month:D2}").ToList();
      case RecordingCoverageGranularity.RecordingCoverageDay:
        var days = await q.Select(c => c.StartTs.Date).Distinct().ToListAsync(ct);
        return days.Select(d => d.ToString("yyyy-MM-dd")).ToList();
      case RecordingCoverageGranularity.RecordingCoverageYear:
      default:
        var years = await q.Select(c => c.StartTs.Year).Distinct().ToListAsync(ct);
        return years.Select(y => y.ToString("D4")).ToList();
    }
  }

  // Timeline coverage: only the boundaries of continuous recording, for drawing.
  //
  // The query projects start/end and nothing else — a day of continuous recording is 1440 clips,
  // and materializing their paths and uids just to collapse them into a handful of intervals is
  // what made the ranges endpoint unusable for a timeline. Merging happens here, so the wire
  // carries intervals, not clips.
  public async Task<List<RecordingTimelineTrack>> GetRecordingTimelineAsync(
    GetRecordingTimelineRequest request, CancellationToken ct)
  {
    var from = request.FromTs.ToDateTime().ToUniversalTime();
    var to = request.ToTs.ToDateTime().ToUniversalTime();
    var minGap = TimeSpan.FromMilliseconds(request.MinGapMs);
    var streamName = request.HasStreamName ? request.StreamName : null;
    var cameraIds = request.CameraIds.Distinct().ToList();

    var tracks = new List<RecordingTimelineTrack>(cameraIds.Count);
    if (cameraIds.Count == 0) return tracks;

    await using var db = await dbFactory.CreateDbContextAsync(ct);

    foreach (var cameraId in cameraIds)
    {
      var q = db.Clips.Where(c => c.CameraId == cameraId && c.Retained);

      if (!string.IsNullOrEmpty(streamName))
        q = q.Where(c => c.StreamName == streamName);

      // A clip that starts before the window but reaches into it still draws inside it.
      q = q.Where(c => (c.EndTs ?? c.StartTs.AddMinutes(1)) >= from && c.StartTs <= to);

      var bounds = await q
        .OrderBy(c => c.StartTs)
        .Select(c => new { c.StartTs, c.EndTs, c.Origin, c.RoiState })
        .ToListAsync(ct);

      var track = new RecordingTimelineTrack { CameraId = cameraId };
      track.Intervals.AddRange(MergeIntervals(
        bounds.Select(b => (b.StartTs, b.EndTs ?? b.StartTs.AddMinutes(1),
          string.IsNullOrWhiteSpace(b.Origin) ? "live" : b.Origin,
          ToRoiState(b.RoiState))), minGap));
      tracks.Add(track);
    }

    return tracks;
  }

  /// <summary>
  /// The stored roi_state as the outcome an operator can see a difference from. Everything
  /// that leaves the file as the camera sent it -- never examined, skipped for want of
  /// boxes, given up on -- looks the same on screen and reports the same.
  /// </summary>
  private static RoiState ToRoiState(string? stored) => stored switch
  {
    "done"    => RoiState.Recompressed,
    "no_gain" => RoiState.NoGain,
    _         => RoiState.Original,
  };

  private static List<RecordingInterval> MergeIntervals(
    IEnumerable<(DateTime Start, DateTime End, string Origin, RoiState RoiState)> ordered,
    TimeSpan minGap)
  {
    var merged = new List<RecordingInterval>();
    DateTime? curStart = null;
    DateTime curEnd = default;
    var curOrigin = "live";
    var curRoi = RoiState.Original;

    foreach (var (start, end, origin, roi) in ordered)
    {
      if (end <= start) continue;

      if (curStart is null)
      {
        curStart = start;
        curEnd = end;
        curOrigin = origin;
        curRoi = roi;
        continue;
      }

      // Provenance breaks a run even when the clips touch: an insert from the camera's own storage
      // is a different kind of footage and the operator has to be able to see where it sits. Having
      // been re-encoded breaks it for the same reason -- and while the pass works through a camera
      // the timeline will show the boundary moving, which is the honest picture.
      if (start - curEnd <= minGap
        && string.Equals(origin, curOrigin, StringComparison.Ordinal)
        && roi == curRoi)
      {
        if (end > curEnd) curEnd = end;
        continue;
      }

      merged.Add(ToInterval(curStart.Value, curEnd, curOrigin, curRoi));
      curStart = start;
      curEnd = end;
      curOrigin = origin;
      curRoi = roi;
    }

    if (curStart is not null)
      merged.Add(ToInterval(curStart.Value, curEnd, curOrigin, curRoi));

    return merged;
  }

  private static RecordingInterval ToInterval(
    DateTime start, DateTime end, string origin, RoiState roiState) => new()
  {
    StartTs = Timestamp.FromDateTime(DateTime.SpecifyKind(start, DateTimeKind.Utc)),
    EndTs = Timestamp.FromDateTime(DateTime.SpecifyKind(end, DateTimeKind.Utc)),
    Origin = origin,
    RoiState = roiState,
  };

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
    {
      // Which end to drop is the whole question. Keeping the first N — what this used to do — throws
      // away the newest recordings, so a day of short outages and their imports is enough to hide
      // everything from the last hour: the clips are in the database and returned by the query, yet
      // invisible in the monitor.
      if (request.Timestamp != null)
      {
        // A moment was asked about, so the window is centred on it.
        var ts = request.Timestamp.ToDateTime().ToUniversalTime();
        var pivot = ranges.FindLastIndex(r => r.Recordings[0].StartTs.ToDateTime() <= ts);
        if (pivot < 0)
          pivot = 0;
        var start = Math.Clamp(pivot - limit / 2, 0, ranges.Count - limit);
        ranges = ranges.GetRange(start, limit);
      }
      else
      {
        // No anchor: the newest is what an operator opening the archive is looking for.
        ranges = ranges.GetRange(ranges.Count - limit, limit);
      }
    }
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

  /// <summary>
  /// Cameras that have at least one clip still waiting on a ROI pass. Asked
  /// first so the camera config is fetched for a handful of cameras rather than
  /// for every camera in the archive.
  /// </summary>
  public async Task<List<string>> GetRoiCameraIdsAsync(CancellationToken ct)
  {
    await using var db = await dbFactory.CreateDbContextAsync(ct);
    return await db.Clips
      .Where(c => c.RoiState == "pending" && c.EndTs != null)
      .Select(c => c.CameraId)
      .Distinct()
      .ToListAsync(ct);
  }

  /// <summary>Whether the clip row is still there. ClipCleaner may have removed it.</summary>
  public async Task<bool> ExistsAsync(Guid clipUid, CancellationToken ct)
  {
    await using var db = await dbFactory.CreateDbContextAsync(ct);
    return await db.Clips.AnyAsync(c => c.ClipUid == clipUid, ct);
  }

  /// <summary>One clip waiting to be re-encoded with ROI, or null if there is nothing to do.</summary>
  public sealed record RoiCandidate(
    Guid ClipUid,
    string FilePath,
    string CameraId,
    string StreamName,
    string StorageId,
    DateTime StartTs,
    DateTime EndTs,
    int Attempts);

  /// <summary>
  /// Finished clips on this node that nobody has re-encoded yet. Deliberately not
  /// reusing GetRecordingsFromToAsync: that one filters on `retained`, and the
  /// clips worth shrinking are the ordinary ones.
  ///
  /// Retained only: an unretained clip is one ClipCleaner is about to delete, so
  /// re-encoding it would be work thrown away — and since the cleaner touches
  /// nothing else, this is also most of the race with it gone.
  ///
  /// Asked per camera, with that camera's own cutoff. A shared query over all of
  /// them would have to use the loosest cutoff and filter afterwards, and a
  /// camera with a long delay and a busy schedule would then fill every batch
  /// with clips that are not eligible yet, starving the cameras that are.
  /// </summary>
  public async Task<RoiCandidate?> GetOldestRoiCandidateAsync(
    string cameraId,
    DateTime closedBefore,
    CancellationToken ct)
  {
    var now = DateTime.UtcNow;

    await using var db = await dbFactory.CreateDbContextAsync(ct);
    return await db.Clips
      .Where(c => c.CameraId == cameraId
        && c.Retained
        && c.RoiState == "pending"
        && c.EndTs != null
        && c.EndTs < closedBefore
        && (c.RoiNextAttemptTs == null || c.RoiNextAttemptTs <= now))
      .OrderBy(c => c.EndTs)
      .Select(c => new RoiCandidate(
        c.ClipUid, c.FilePath, c.CameraId, c.StreamName, c.StorageId,
        c.StartTs, c.EndTs!.Value, c.RoiAttempts))
      .FirstOrDefaultAsync(ct);
  }

  /// <summary>
  /// Records the outcome without touching the file's own columns. `state` is one
  /// of the values the 0009 migration lists.
  /// </summary>
  public async Task SetRoiStateAsync(
    Guid clipUid, string state, DateTime? nextAttempt, bool countAttempt,
    float? coverage, CancellationToken ct)
  {
    await using var db = await dbFactory.CreateDbContextAsync(ct);
    await db.Clips
      .Where(c => c.ClipUid == clipUid)
      .ExecuteUpdateAsync(u => u
        .SetProperty(c => c.RoiState, state)
        .SetProperty(c => c.RoiNextAttemptTs, nextAttempt)
        .SetProperty(c => c.RoiAttempts, c => countAttempt ? c.RoiAttempts + 1 : c.RoiAttempts)
        .SetProperty(c => c.RoiCoverage, c => coverage ?? c.RoiCoverage),
        ct);
  }

  /// <summary>
  /// The clip's file has been replaced by a smaller one. Updates only what the
  /// new file changed.
  ///
  /// start_ts and end_ts are deliberately untouched: the 0008 trigger purges
  /// clip_metadata_chunks outside the clip's window whenever either moves, which
  /// would delete the very boxes the re-encode was built from. frag_index goes to
  /// null because the cached fragment byte ranges describe the old file.
  ///
  /// Returns false when the clip row is gone -- ClipCleaner may have deleted it
  /// while the job ran, and that is an ordinary outcome, not an error.
  /// </summary>
  public async Task<bool> CompleteRoiTranscodeAsync(
    Guid clipUid, long sizeBytes, float coverage, CancellationToken ct)
  {
    await using var db = await dbFactory.CreateDbContextAsync(ct);
    var affected = await db.Clips
      .Where(c => c.ClipUid == clipUid)
      .ExecuteUpdateAsync(u => u
        .SetProperty(c => c.SizeBytes, sizeBytes)
        .SetProperty(c => c.FragIndex, (string?)null)
        .SetProperty(c => c.RoiState, "done")
        .SetProperty(c => c.RoiCoverage, coverage)
        .SetProperty(c => c.RoiNextAttemptTs, (DateTime?)null),
        ct);
    return affected > 0;
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

  // Returns null when the episode does not exist; empty list when it exists but has no retained
  // clips yet. Scoped to a single episode (event_uuid), i.e. the one row the user selected.
  public async Task<List<Recording>?> GetClipsForEventAsync(Guid eventUuid, CancellationToken ct)
  {
    await using var evtCmd = dataSource.CreateCommand("""
      SELECT camera_id FROM retention_events WHERE event_uuid = $1;
      """);
    evtCmd.Parameters.AddWithValue(eventUuid);
    var cameraId = (string?)await evtCmd.ExecuteScalarAsync(ct);
    if (cameraId is null)
      return null;

    var config = await configCache.GetAsync(cameraId, ct);
    var pre  = config?.PreRecordSeconds  ?? 30;
    var post = config?.PostRecordSeconds ?? 30;

    await using var cmd = dataSource.CreateCommand("""
      SELECT c.camera_id, c.stream_name, c.file_path, c.start_ts, c.end_ts, c.size_bytes, c.clip_uid::text, c.storage_id, c.origin,
             '',
             c.roi_state
      FROM clips c
      JOIN retention_events re ON re.event_uuid = $1
        AND re.camera_id = c.camera_id
        AND c.start_ts <= re.alarm_end_ts + ($2 * interval '1 second')
        AND COALESCE(c.end_ts, c.start_ts) >= re.alarm_start_ts - ($3 * interval '1 second')
      WHERE c.retained = TRUE
      ORDER BY c.stream_name, c.start_ts;
      """);
    cmd.Parameters.AddWithValue(eventUuid);
    cmd.Parameters.AddWithValue(post);
    cmd.Parameters.AddWithValue(pre);

    var rows = await ReadRecordingWithEventsAsync(cmd, ct);
    return rows.Select(r => r.Recording).ToList();
  }

  public async Task<Dictionary<Guid, (string StorageId, string FilePath, string? FragIndex)>> GetClipsForIndexAsync(
    IReadOnlyList<Guid> uids, CancellationToken ct)
  {
    await using var db = await dbFactory.CreateDbContextAsync(ct);
    var rows = await db.Clips
      .Where(c => uids.Contains(c.ClipUid))
      .Select(c => new { c.ClipUid, c.StorageId, c.FilePath, c.FragIndex })
      .ToListAsync(ct);
    return rows.ToDictionary(r => r.ClipUid, r => (r.StorageId, r.FilePath, r.FragIndex));
  }

  /// <summary>
  /// Finishes clip rows the previous run of the media server left open, from the files themselves.
  /// Returns how many were repaired.
  /// </summary>
  /// <remarks>
  /// A killed writer never sends the finalizing ingest, so its last clip keeps <c>end_ts</c> NULL
  /// and size 0 — and an unfinished clip is read as covering the next minute, which is exactly the
  /// stretch the restart lost. The footage is really there, though: the fragments are written as
  /// they go and only the trailer is missing, so the extent is measured by walking them. That both
  /// tells the truth about coverage and makes the recording playable, which without an index it is
  /// not.
  /// </remarks>
  /// <returns>
  /// How many rows were repaired, and the span the archive had been assuming for them — a gap
  /// already judged against that assumption has to be looked at again.
  /// </returns>
  public async Task<(int Repaired, int Deferred, DateTime From, DateTime To)> FinalizeDanglingClipsAsync(
    string cameraId, string streamName, DateTime notAfter, CancellationToken ct)
  {
    await using var db = await dbFactory.CreateDbContextAsync(ct);
    var dangling = await db.Clips
      .Where(c => c.CameraId == cameraId
        && c.StreamName == streamName
        && c.EndTs == null
        && c.StartTs <= notAfter)
      .ToListAsync(ct);
    if (dangling.Count == 0)
      return (0, 0, default, default);

    var storages = await storageListCache.GetAsync(ct);
    var repaired = 0;
    var deferred = 0;

    foreach (var clip in dangling)
    {
      var root = storageListCache.Find(storages, clip.StorageId)?.RootPath;
      var absPath = string.IsNullOrWhiteSpace(root) ? null : Path.Combine(root, clip.FilePath);

      // The storage list has not loaded yet, which says nothing about the clip. Writing it off as
      // zero-length would be permanent — the row stops being dangling and is never looked at again —
      // so it waits for a pass that can actually open the file.
      if (absPath is null)
      {
        deferred++;
        continue;
      }

      // Nothing to measure — the row claims no time rather than a minute of it.
      var end = clip.StartTs;
      long size = clip.SizeBytes;

      if (File.Exists(absPath))
      {
        // A file that was touched a moment ago has a writer on it: the run that owns it is alive and
        // its length is not final yet. Only quiet on disk proves otherwise, and there is no hurry —
        // the boundary comes from the archive, not from when we happened to look.
        if (DateTime.UtcNow - File.GetLastWriteTimeUtc(absPath) < StillBeingWritten)
        {
          deferred++;
          continue;
        }

        size = new FileInfo(absPath).Length;
        var idx = await ClipIndexParser.ReadAsync(absPath, ct);
        if (idx is { Fragments.Count: > 0 })
        {
          var seconds = idx.Fragments.Sum(f => f.Duration);
          end = clip.StartTs.AddSeconds(seconds);
          clip.FragIndex = ClipIndexParser.Serialize(idx);
        }
      }

      clip.EndTs = end;
      clip.SizeBytes = size;
      repaired++;
    }

    await db.SaveChangesAsync(ct);

    // An unfinished clip was read as covering the minute after its start; that is the window whose
    // verdicts were based on something no longer true.
    var from = dangling.Min(c => c.StartTs);
    var to = dangling.Max(c => c.StartTs).AddMinutes(1);
    return (repaired, deferred, from, to);
  }

  /// <summary>
  /// Clips left open by a run that is gone, together with the moment recording resumed.
  /// </summary>
  /// <remarks>
  /// A later clip on the same stream is the proof: a writer that moved on to a new segment is never
  /// coming back to this one, so the row can be measured and the stretch between the two treated as
  /// lost. Nothing else in the system can say this after a node-wide restart — the heartbeats and the
  /// instance id they carry live in memory that went down with the media server, while the archive
  /// is still on disk.
  /// </remarks>
  public async Task<List<AbandonedClip>> ListAbandonedAsync(
    IReadOnlyCollection<string> cameraIds, CancellationToken ct)
  {
    if (cameraIds.Count == 0)
      return [];

    await using var cmd = dataSource.CreateCommand("""
      SELECT c.camera_id, c.stream_name, c.start_ts,
             (SELECT MIN(n.start_ts) FROM clips n
               WHERE n.camera_id = c.camera_id
                 AND n.stream_name = c.stream_name
                 AND n.start_ts > c.start_ts) AS resumed_at
      FROM clips c
      WHERE c.end_ts IS NULL AND c.camera_id = ANY($1)
      ORDER BY c.start_ts;
      """);
    cmd.Parameters.AddWithValue(NpgsqlDbType.Array | NpgsqlDbType.Text, cameraIds.ToArray());

    var result = new List<AbandonedClip>();
    await using var reader = await cmd.ExecuteReaderAsync(ct);
    while (await reader.ReadAsync(ct))
    {
      // A later clip is not required. Nothing has written to this one since the run that owned it
      // went away, and that is established by the file being quiet rather than by recording having
      // resumed — waiting for the next clip would leave the hole unrecorded for as long as the
      // camera stays down, which is exactly when an operator goes looking for it.
      result.Add(new AbandonedClip(
        reader.GetString(0), reader.GetString(1), reader.GetDateTime(2),
        reader.IsDBNull(3) ? null : reader.GetDateTime(3)));
    }
    return result;
  }

  public async Task SaveFragIndexAsync(Guid uid, string json, CancellationToken ct)
  {
    await using var db = await dbFactory.CreateDbContextAsync(ct);
    await db.Clips.Where(c => c.ClipUid == uid)
      .ExecuteUpdateAsync(s => s.SetProperty(c => c.FragIndex, json), ct);
  }

  // Per-stream retention breakdown for a camera: a retention_events row with NULL stream_name retains
  // every stream (AllStreams=true), otherwise only the named streams are retained right now.
  public async Task<(bool AllStreams, List<string> Streams)> GetRetainedStreamsAsync(string cameraId, CancellationToken ct)
  {
    var config = await configCache.GetAsync(cameraId, ct);
    var post = config?.PostRecordSeconds ?? 30;

    await using var cmd = dataSource.CreateCommand("""
      SELECT stream_name FROM retention_events
      WHERE camera_id = $1
        AND alarm_start_ts <= now()
        AND alarm_end_ts + ($2 * interval '1 second') >= now()
      GROUP BY stream_name;
      """);
    cmd.Parameters.AddWithValue(cameraId);
    cmd.Parameters.AddWithValue(post);

    var allStreams = false;
    var streams = new List<string>();
    await using var reader = await cmd.ExecuteReaderAsync(ct);
    while (await reader.ReadAsync(ct))
    {
      if (reader.IsDBNull(0)) allStreams = true;
      else streams.Add(reader.GetString(0));
    }
    return (allStreams, streams);
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

  // All episodes for the given event_id groups — used to complete groups whose episodes straddle the
  // gateway's paged window (so badge counts / earliest timestamps are accurate).
  public async Task<List<RetentionEventRow>> GetEpisodesByEventIdsAsync(
    IReadOnlyList<string> eventIds, string? cameraId, CancellationToken ct)
  {
    if (eventIds.Count == 0)
      return [];

    // An event_id groups one object, which analytics may have tracked across several cameras, so a
    // camera-scoped caller must be able to say so here as well.
    var byCamera = !string.IsNullOrWhiteSpace(cameraId);
    await using var cmd = dataSource.CreateCommand($"""
      SELECT event_id, camera_id, stream_name, alarm_start_ts, alarm_end_ts, event_uuid::text, object_id, trigger
      FROM retention_events
      WHERE event_id = ANY($1){(byCamera ? " AND camera_id = $2" : "")}
      ORDER BY alarm_start_ts DESC, event_uuid DESC;
      """);
    cmd.Parameters.AddWithValue(eventIds.ToArray());
    if (byCamera) cmd.Parameters.AddWithValue(cameraId!);

    var events = new List<RetentionEventRow>();
    await using var reader = await cmd.ExecuteReaderAsync(ct);
    while (await reader.ReadAsync(ct))
    {
      var row = new RetentionEventRow
      {
        EventId      = reader.GetString(0),
        CameraId     = reader.GetString(1),
        AlarmStartTs = Timestamp.FromDateTime(reader.GetDateTime(3).ToUniversalTime()),
        EventUuid    = reader.GetString(5)
      };
      if (!reader.IsDBNull(2)) row.StreamName = reader.GetString(2);
      if (!reader.IsDBNull(4)) row.AlarmEndTs = Timestamp.FromDateTime(reader.GetDateTime(4).ToUniversalTime());
      if (!reader.IsDBNull(6)) row.ObjectId = reader.GetString(6);
      if (!reader.IsDBNull(7)) row.Trigger = reader.GetString(7);
      events.Add(row);
    }
    return events;
  }

  public async Task<(List<RetentionEventRow> Events, bool HasMore, Timestamp? NextBefore)> ListRetentionEventsAsync(
    uint limit,
    Timestamp? beforeAlarmStart,
    string? cameraId,
    Timestamp? fromAlarmStart,
    Timestamp? endingAfter,
    CancellationToken ct)
  {
    var lim = limit == 0 || limit > 200 ? 50 : (int)limit;

    // Built up rather than written out: the filter combinations the timeline and the events
    // tab need would otherwise be near-identical literals. Values stay parameterized.
    var where = new List<string>();
    var values = new List<object>();

    if (beforeAlarmStart is not null)
    {
      values.Add(beforeAlarmStart.ToDateTime());
      where.Add($"alarm_start_ts < ${values.Count}");
    }
    if (!string.IsNullOrWhiteSpace(cameraId))
    {
      values.Add(cameraId);
      where.Add($"camera_id = ${values.Count}");
    }
    if (fromAlarmStart is not null)
    {
      values.Add(fromAlarmStart.ToDateTime());
      where.Add($"alarm_start_ts >= ${values.Count}");
    }
    if (endingAfter is not null)
    {
      values.Add(endingAfter.ToDateTime());
      where.Add($"alarm_end_ts >= ${values.Count}");
    }

    values.Add(lim);
    var sql = $"""
      SELECT event_id, camera_id, stream_name, alarm_start_ts, alarm_end_ts, event_uuid::text, object_id, trigger
      FROM retention_events
      {(where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : "")}
      ORDER BY alarm_start_ts DESC, event_uuid DESC
      LIMIT ${values.Count};
      """;

    await using var cmd = dataSource.CreateCommand(sql);
    foreach (var v in values) cmd.Parameters.AddWithValue(v);

    var events = new List<RetentionEventRow>();
    await using var reader = await cmd.ExecuteReaderAsync(ct);
    while (await reader.ReadAsync(ct))
    {
      var row = new RetentionEventRow
      {
        EventId      = reader.GetString(0),
        CameraId     = reader.GetString(1),
        AlarmStartTs = Timestamp.FromDateTime(reader.GetDateTime(3).ToUniversalTime()),
        EventUuid    = reader.GetString(5)
      };
      if (!reader.IsDBNull(2)) row.StreamName = reader.GetString(2);
      if (!reader.IsDBNull(4)) row.AlarmEndTs = Timestamp.FromDateTime(reader.GetDateTime(4).ToUniversalTime());
      if (!reader.IsDBNull(6)) row.ObjectId = reader.GetString(6);
      if (!reader.IsDBNull(7)) row.Trigger = reader.GetString(7);
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
    // The limit takes from the NEWEST end and the result is turned back around afterwards. Taking
    // the oldest rows instead — which is what an ascending LIMIT does — silently hides the most
    // recent footage the moment the window holds more clips than the limit: they are in the
    // database and matched by the query, yet absent from the answer. Callers still receive the page
    // in ascending order, which is what the range grouping expects.
    await using var cmd = dataSource.CreateCommand($"""
      SELECT * FROM (
        SELECT c.camera_id, c.stream_name, c.file_path, c.start_ts, c.end_ts, c.size_bytes, c.clip_uid::text, c.storage_id, c.origin,
               COALESCE(string_agg(DISTINCT re.event_id, ','), '') AS event_ids,
               c.roi_state
        FROM clips c
        LEFT JOIN retention_events re ON re.camera_id = c.camera_id
          AND c.start_ts <= re.alarm_end_ts + ($5 * interval '1 second')
          AND COALESCE(c.end_ts, c.start_ts) >= re.alarm_start_ts - ($6 * interval '1 second')
        WHERE c.camera_id = $1 AND c.retained = TRUE {streamFilter}
          AND ($2::timestamptz IS NULL OR COALESCE(c.end_ts, c.start_ts + interval '1 minute') >= $2)
          AND ($3::timestamptz IS NULL OR c.start_ts <= $3)
        GROUP BY c.clip_uid
        ORDER BY c.start_ts DESC
        LIMIT $4
      ) page
      ORDER BY start_ts ASC;
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
      SELECT c.camera_id, c.stream_name, c.file_path, c.start_ts, c.end_ts, c.size_bytes, c.clip_uid::text, c.storage_id, c.origin,
             COALESCE(string_agg(DISTINCT re.event_id, ','), ''),
             c.roi_state
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
        StorageId  = reader.IsDBNull(7) ? "" : reader.GetString(7),
        Origin     = reader.IsDBNull(8) ? "" : reader.GetString(8),
        // Column 10, appended rather than slotted in beside origin: this reader
        // is shared by three queries and indexes by position, so inserting a
        // column would silently shift every field after it.
        RoiState = ToRoiState(reader.IsDBNull(10) ? null : reader.GetString(10))
      };
      if (!reader.IsDBNull(4))
        rec.EndTs = Timestamp.FromDateTime(reader.GetDateTime(4).ToUniversalTime());

      var ev = reader.IsDBNull(9) ? "" : reader.GetString(9);
      var ids = string.IsNullOrEmpty(ev)
        ? new List<string>()
        : ev.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct().ToList();
      list.Add(new RecordingWithEvents(rec, ids));
    }
    return list;
  }

  private static string NormalizedOrigin(Recording clip) =>
    string.IsNullOrWhiteSpace(clip.Origin) ? "live" : clip.Origin;

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

      // An insert pulled off the camera is its own range even when it butts up against live
      // footage: it was recorded elsewhere, and it is allowed to overlap by a second or two, which
      // a single continuous range cannot express.
      var originChanged = !string.Equals(
        NormalizedOrigin(prevRec), NormalizedOrigin(clip), StringComparison.Ordinal);

      if (spanExceeded || pauseTooLong || originChanged)
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
    StorageId  = c.StorageId,
    RoiState = ToRoiState(c.RoiState)
  };
}
