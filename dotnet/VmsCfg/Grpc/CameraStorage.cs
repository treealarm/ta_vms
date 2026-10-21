using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using Vms.Cfg;

namespace VmsCfg;

public class CameraStorage(
  NpgsqlDataSource dataSource,
  IDbContextFactory<VmsCfgDbContext> dbFactory,
  ScheduleCache scheduleCache,
  StreamStatsCache streamStats,
  ILogger<CameraStorage> logger)
{
  private readonly NpgsqlDataSource _dataSource = dataSource;
  private static readonly Guid BuiltinContinuousProfileId = new("00000000-0000-0000-0000-000000000001");
  private static readonly Guid BuiltinPrealarmProfileId   = new("00000000-0000-0000-0000-000000000002");
  private static readonly Guid BuiltinOffProfileId        = new("00000000-0000-0000-0000-000000000003");

  private readonly IReadOnlyList<(string ServerId, string FsId, string OnvifId, string LoggerId, string RoiId)> _serverPairs = ParseServerPairs();

  /// One entry per node: the media server, the VmsFs that owns its archive volume, the VmsOnvif that
  /// shares its camera subnet, the VmsLogger that can read its log files, and optionally the
  /// roitrc that can reach the same disk. All of them are pinned to
  /// the same machine, so the entry is the node — there is no arrangement in which they can be
  /// picked independently.
  private static IReadOnlyList<(string ServerId, string FsId, string OnvifId, string LoggerId, string RoiId)> ParseServerPairs()
  {
    var raw = Environment.GetEnvironmentVariable("MEDIA_SERVER_PAIRS")
      ?? throw new InvalidOperationException("Required environment variable 'MEDIA_SERVER_PAIRS' is not set");

    var pairs = new List<(string, string, string, string, string)>();
    foreach (var entry in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    {
      // Rejected rather than defaulted: an entry missing a part leaves web_srv.Server with no
      // instance to send PTZ to, or no instance that can read that node's logs, and there is nothing
      // sensible to guess — a central instance can reach neither.
      // The fifth part is the ROI transcoder and is optional — a node that does not re-compress
      // simply leaves it off. The first four are not: an entry missing one of those leaves
      // web_srv.Server with no instance to send PTZ to, or none that can read that node's logs,
      // and there is nothing sensible to guess.
      var parts = entry.Split(':', StringSplitOptions.TrimEntries);
      if (parts.Length is not (4 or 5) || parts.Take(4).Any(string.IsNullOrEmpty))
        throw new InvalidOperationException(
          $"Invalid MEDIA_SERVER_PAIRS entry '{entry}': expected "
          + "'server_id:fs_id:onvif_id:logger_id' with an optional ':roi_id'");
      pairs.Add((parts[0], parts[1], parts[2], parts[3], parts.Length == 5 ? parts[4] : string.Empty));
    }

    if (pairs.Count == 0)
      throw new InvalidOperationException(
        "MEDIA_SERVER_PAIRS must contain at least one 'server_id:fs_id:onvif_id:logger_id' entry");

    return pairs;
  }

  public IReadOnlyList<(string ServerId, string FsId, string OnvifId, string LoggerId, string RoiId)> GetServerPairs() => _serverPairs;

  public bool IsValidServerId(string serverId) =>
    _serverPairs.Any(p => p.ServerId == serverId);

  public async Task SetupCameraAsync(CameraSetup camera, CancellationToken ct)
  {
    await using var connection = await _dataSource.OpenConnectionAsync(ct);
    await using var tx = await connection.BeginTransactionAsync(ct);

    Guid? mediaMtxServerId = !string.IsNullOrEmpty(camera.MediaMtxServerId)
      && Guid.TryParse(camera.MediaMtxServerId, out var parsedMmx) ? parsedMmx : null;

    await using (var command = new NpgsqlCommand("""
      INSERT INTO camera_setup (camera_id, server_id, cred_user, cred_pass, onvif_ip, onvif_port, onvif_xaddr, media_mtx_server_id, disabled, name, edge_backfill_stream, roi_transcode, roi_delay_hours, roi_min_coverage, roi_max_coverage, onvif_metadata_disabled, has_telemetry)
      VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14, $15, $16, $17)
      ON CONFLICT (camera_id) DO UPDATE SET
        server_id           = EXCLUDED.server_id,
        cred_user           = EXCLUDED.cred_user,
        cred_pass           = EXCLUDED.cred_pass,
        onvif_ip            = EXCLUDED.onvif_ip,
        onvif_port          = EXCLUDED.onvif_port,
        onvif_xaddr         = EXCLUDED.onvif_xaddr,
        media_mtx_server_id = EXCLUDED.media_mtx_server_id,
        disabled            = EXCLUDED.disabled,
        name                = EXCLUDED.name,
        edge_backfill_stream = EXCLUDED.edge_backfill_stream,
        roi_transcode       = EXCLUDED.roi_transcode,
        roi_delay_hours     = EXCLUDED.roi_delay_hours,
        roi_min_coverage    = EXCLUDED.roi_min_coverage,
        roi_max_coverage    = EXCLUDED.roi_max_coverage,
        onvif_metadata_disabled = EXCLUDED.onvif_metadata_disabled,
        has_telemetry       = EXCLUDED.has_telemetry;
      """, connection, tx))
    {
      command.Parameters.AddWithValue(camera.CameraId);
      command.Parameters.AddWithValue(camera.ServerId ?? string.Empty);
      command.Parameters.AddWithValue((object?)camera.Cred?.User ?? DBNull.Value);
      command.Parameters.AddWithValue((object?)camera.Cred?.Pass ?? DBNull.Value);
      command.Parameters.AddWithValue((object?)camera.OnvifConn?.Ip ?? DBNull.Value);
      command.Parameters.AddWithValue((object?)camera.OnvifConn?.Port ?? DBNull.Value);
      command.Parameters.AddWithValue((object?)(string.IsNullOrEmpty(camera.OnvifConn?.Xaddr) ? null : camera.OnvifConn.Xaddr) ?? DBNull.Value);
      command.Parameters.Add(new NpgsqlParameter { Value = (object?)mediaMtxServerId ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Uuid });
      command.Parameters.Add(new NpgsqlParameter { Value = (camera.HasDisabled && camera.Disabled) ? (object)true : DBNull.Value, NpgsqlDbType = NpgsqlDbType.Boolean });
      command.Parameters.AddWithValue(camera.Name ?? string.Empty);
      command.Parameters.AddWithValue(camera.EdgeBackfillStream ?? string.Empty);
      command.Parameters.AddWithValue(camera.RoiTranscode);
      command.Parameters.AddWithValue(camera.RoiDelayHours);
      command.Parameters.AddWithValue(camera.RoiMinCoverage);
      command.Parameters.AddWithValue(camera.RoiMaxCoverage);
      command.Parameters.AddWithValue(camera.OnvifMetadataDisabled);
      command.Parameters.AddWithValue(camera.HasTelemetry);
      await command.ExecuteNonQueryAsync(ct);
    }

    await UpsertStreamsAsync(connection, tx, camera.CameraId, camera.Streams, ct);

    await tx.CommitAsync(ct);
  }

  private static async Task UpsertStreamsAsync(
    NpgsqlConnection connection,
    NpgsqlTransaction tx,
    string cameraId,
    IEnumerable<CameraStream> streams,
    CancellationToken ct)
  {
    var streamList = streams.ToList();
    var keepNames = streamList.Select(s => s.StreamName).ToArray();

    await using (var deleteCmd = new NpgsqlCommand("""
      DELETE FROM camera_stream
      WHERE camera_id = $1 AND NOT (stream_name = ANY($2));
      """, connection, tx))
    {
      deleteCmd.Parameters.AddWithValue(cameraId);
      deleteCmd.Parameters.AddWithValue(keepNames);
      await deleteCmd.ExecuteNonQueryAsync(ct);
    }

    foreach (var stream in streamList)
    {
      Guid? profileId = stream.RecordingProfile != null && !string.IsNullOrEmpty(stream.RecordingProfile.Id)
        ? Guid.Parse(stream.RecordingProfile.Id)
        : null;

      await using var cmd = new NpgsqlCommand("""
        INSERT INTO camera_stream (camera_id, stream_name, profile_token, path, web_rtc, recording_profile_id, disabled, recordable, declared_width, declared_height)
        VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10)
        ON CONFLICT (camera_id, stream_name) DO UPDATE SET
          profile_token        = EXCLUDED.profile_token,
          path                 = EXCLUDED.path,
          web_rtc              = EXCLUDED.web_rtc,
          recording_profile_id = EXCLUDED.recording_profile_id,
          disabled             = EXCLUDED.disabled,
          recordable           = EXCLUDED.recordable,
          -- Zero means "the caller does not know", not "the camera declares nothing": most callers
          -- re-send the whole setup without ever having asked ONVIF, and must not erase what a
          -- discovery or an encoder-settings change learned.
          declared_width       = CASE WHEN EXCLUDED.declared_width  > 0 THEN EXCLUDED.declared_width  ELSE camera_stream.declared_width  END,
          declared_height      = CASE WHEN EXCLUDED.declared_height > 0 THEN EXCLUDED.declared_height ELSE camera_stream.declared_height END;
        """, connection, tx);
      cmd.Parameters.AddWithValue(cameraId);
      cmd.Parameters.AddWithValue(stream.StreamName);
      cmd.Parameters.AddWithValue(stream.ProfileToken ?? string.Empty);
      cmd.Parameters.AddWithValue(stream.Path ?? string.Empty);
      cmd.Parameters.AddWithValue(stream.WebRtc);
      cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)profileId ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Uuid });
      cmd.Parameters.Add(new NpgsqlParameter { Value = (stream.HasDisabled && stream.Disabled) ? (object)true : DBNull.Value, NpgsqlDbType = NpgsqlDbType.Boolean });
      // Unset means recordable: a caller that does not know about the flag must not clear it.
      cmd.Parameters.AddWithValue(!stream.HasRecordable || stream.Recordable);
      cmd.Parameters.AddWithValue(stream.DeclaredWidth);
      cmd.Parameters.AddWithValue(stream.DeclaredHeight);
      await cmd.ExecuteNonQueryAsync(ct);
    }
  }

  public async Task<List<CameraSetup>> ListCamerasAsync(string serverId, CancellationToken ct)
  {
    await using var connection = await _dataSource.OpenConnectionAsync(ct);

    var cameras = new List<CameraSetup>();

    await using (var cmd = new NpgsqlCommand("""
      SELECT camera_setup.camera_id, camera_setup.server_id, cred_user, cred_pass, onvif_ip, onvif_port,
             onvif_xaddr, media_mtx_server_id,
             EXISTS(SELECT 1 FROM onvif_event_subscription s WHERE s.camera_id = camera_setup.camera_id AND s.subscribed = true) AS onvif_events_enabled,
             EXISTS(SELECT 1 FROM onvif_alarm_filter f WHERE f.camera_id = camera_setup.camera_id) AS has_onvif_filters,
             EXISTS(SELECT 1 FROM onvif_event_subscription s WHERE s.camera_id = camera_setup.camera_id) AS onvif_managed,
             disabled,
             COALESCE(oes.poll_timeout_seconds, 5) AS poll_timeout_seconds,
             COALESCE(oes.message_limit, 1024) AS message_limit,
             camera_setup.name,
             mtx.stun_url,
             camera_setup.edge_backfill_stream,
             camera_setup.roi_transcode,
             camera_setup.roi_delay_hours,
             camera_setup.roi_min_coverage,
             camera_setup.roi_max_coverage,
             camera_setup.onvif_metadata_disabled,
             camera_setup.has_telemetry
      FROM camera_setup
      LEFT JOIN onvif_event_subscription oes ON oes.camera_id = camera_setup.camera_id
      LEFT JOIN media_mtx_server mtx ON mtx.id = camera_setup.media_mtx_server_id
      WHERE $1 = '' OR camera_setup.server_id = $1
      ORDER BY camera_setup.name, camera_setup.camera_id;
      """, connection))
    {
      cmd.Parameters.AddWithValue(serverId ?? string.Empty);
      await using var reader = await cmd.ExecuteReaderAsync(ct);
      while (await reader.ReadAsync(ct))
        cameras.Add(ReadCameraSetup(reader));
    }

    await LoadStreamsAsync(connection, cameras, ct);
    await scheduleCache.EnsureFreshAsync(ct);
    ApplySchedules(cameras);
    await streamStats.ApplyAsync(cameras, ct);
    return cameras;
  }

  public async Task<(bool Found, CameraSetup? Setup)> GetCameraAsync(string cameraId, CancellationToken ct)
  {
    await using var connection = await _dataSource.OpenConnectionAsync(ct);

    CameraSetup? setup = null;

    await using (var cmd = new NpgsqlCommand("""
      SELECT camera_setup.camera_id, camera_setup.server_id, cred_user, cred_pass, onvif_ip, onvif_port,
             onvif_xaddr, media_mtx_server_id,
             EXISTS(SELECT 1 FROM onvif_event_subscription s WHERE s.camera_id = camera_setup.camera_id AND s.subscribed = true) AS onvif_events_enabled,
             EXISTS(SELECT 1 FROM onvif_alarm_filter f WHERE f.camera_id = camera_setup.camera_id) AS has_onvif_filters,
             EXISTS(SELECT 1 FROM onvif_event_subscription s WHERE s.camera_id = camera_setup.camera_id) AS onvif_managed,
             disabled,
             COALESCE(oes.poll_timeout_seconds, 5) AS poll_timeout_seconds,
             COALESCE(oes.message_limit, 1024) AS message_limit,
             camera_setup.name,
             mtx.stun_url,
             camera_setup.edge_backfill_stream,
             camera_setup.roi_transcode,
             camera_setup.roi_delay_hours,
             camera_setup.roi_min_coverage,
             camera_setup.roi_max_coverage,
             camera_setup.onvif_metadata_disabled,
             camera_setup.has_telemetry
      FROM camera_setup
      LEFT JOIN onvif_event_subscription oes ON oes.camera_id = camera_setup.camera_id
      LEFT JOIN media_mtx_server mtx ON mtx.id = camera_setup.media_mtx_server_id
      WHERE camera_setup.camera_id = $1;
      """, connection))
    {
      cmd.Parameters.AddWithValue(cameraId);
      await using var reader = await cmd.ExecuteReaderAsync(ct);
      if (await reader.ReadAsync(ct))
        setup = ReadCameraSetup(reader);
    }

    if (setup is null)
      return (false, null);

    await LoadStreamsAsync(connection, [setup], ct);
    await scheduleCache.EnsureFreshAsync(ct);
    ApplySchedules([setup]);
    await streamStats.ApplyAsync([setup], ct);
    return (true, setup);
  }

  /// The single place where a schedule becomes a fact: schedule_active is resolved from the wall
  /// clock every time a setup is read, and never stored. Consumers (video_srv, VmsFs,
  /// web_srv.Server) treat it as "record continuously, overriding profile and overrides".
  private void ApplySchedules(IReadOnlyList<CameraSetup> cameras)
  {
    var now = DateTime.UtcNow;
    foreach (var camera in cameras)
    {
      camera.ScheduleIds.Clear();
      camera.ScheduleIds.AddRange(scheduleCache.GetCameraScheduleIds(camera.CameraId));
      camera.ScheduleActive = scheduleCache.IsCameraActive(camera.CameraId, now);
    }
  }

  public async Task<bool> RemoveCameraAsync(string cameraId, CancellationToken ct)
  {
    await using var command = _dataSource.CreateCommand(
      "DELETE FROM camera_setup WHERE camera_id = $1;");
    command.Parameters.AddWithValue(cameraId);
    return await command.ExecuteNonQueryAsync(ct) > 0;
  }

  public async Task<List<RecordingProfile>> ListRecordingProfilesAsync(CancellationToken ct)
  {
    await using var command = _dataSource.CreateCommand("""
      SELECT id, name, mode, pre_record_seconds, post_record_seconds, segment_seconds, is_override_preset, is_builtin
      FROM recording_profile
      ORDER BY name;
      """);

    var profiles = new List<RecordingProfile>();
    await using var reader = await command.ExecuteReaderAsync(ct);
    while (await reader.ReadAsync(ct))
      profiles.Add(ReadRecordingProfile(reader));
    return profiles;
  }

  public async Task UpsertRecordingProfileAsync(RecordingProfile profile, CancellationToken ct)
  {
    await using var command = _dataSource.CreateCommand("""
      INSERT INTO recording_profile (id, name, mode, pre_record_seconds, post_record_seconds, segment_seconds, is_override_preset)
      VALUES (COALESCE(NULLIF($1, '')::uuid, gen_random_uuid()), $2, $3, $4, $5, $6, $7)
      ON CONFLICT (id) DO UPDATE SET
        name                = EXCLUDED.name,
        mode                = EXCLUDED.mode,
        pre_record_seconds  = EXCLUDED.pre_record_seconds,
        post_record_seconds = EXCLUDED.post_record_seconds,
        segment_seconds     = EXCLUDED.segment_seconds,
        is_override_preset  = EXCLUDED.is_override_preset
      WHERE recording_profile.is_builtin = false;
      """);
    command.Parameters.AddWithValue(profile.Id ?? string.Empty);
    command.Parameters.AddWithValue(profile.Name);
    command.Parameters.AddWithValue(RecordingModeToDb(profile.Mode));
    command.Parameters.AddWithValue((int)profile.PreRecordSeconds);
    command.Parameters.AddWithValue((int)profile.PostRecordSeconds);
    command.Parameters.AddWithValue((int)Math.Max(1u, profile.SegmentSeconds));
    command.Parameters.AddWithValue(profile.IsOverridePreset);
    await command.ExecuteNonQueryAsync(ct);
  }

  public async Task SetStreamOverrideAsync(string cameraId, string streamName, string profileId, CancellationToken ct)
  {
    await using var command = _dataSource.CreateCommand("""
      -- alarm_override_until is cleared with it: a manual override lasts until it is taken away,
      -- and inheriting a deadline left by a timed one would end it at a moment nobody chose.
      UPDATE camera_stream
      SET override_profile_id = $3::uuid, alarm_override_until = NULL
      WHERE camera_id = $1 AND stream_name = $2;
      """);
    command.Parameters.AddWithValue(cameraId);
    command.Parameters.AddWithValue(streamName);
    command.Parameters.AddWithValue(profileId);
    await command.ExecuteNonQueryAsync(ct);
  }

  public async Task ClearStreamOverrideAsync(string cameraId, string streamName, CancellationToken ct)
  {
    await using var command = _dataSource.CreateCommand("""
      -- Also drops the deadline: leaving it behind would have the expiry sweep act on a stream
      -- whose override is already gone.
      UPDATE camera_stream
      SET override_profile_id = NULL, alarm_override_until = NULL
      WHERE camera_id = $1 AND stream_name = $2;
      """);
    command.Parameters.AddWithValue(cameraId);
    command.Parameters.AddWithValue(streamName);
    await command.ExecuteNonQueryAsync(ct);
  }

  /// <summary>
  /// Switches continuous recording on or off across every enabled, recordable stream of one
  /// camera, in one statement. Streams that already hold the target profile are not touched and are
  /// not reported, so a repeated click publishes nothing.
  ///
  /// Turning it off returns the streams to the pre-alarm profile when the camera is armed, and to
  /// off otherwise — the same choice the per-stream path has always made, kept here so a caller
  /// cannot get it wrong and so the armed state is read inside the same transaction as the write.
  ///
  /// Raw SQL because the set of rows to touch is defined by a join over three tables and the answer
  /// must name the rows that actually changed (see the raw-SQL exception in CLAUDE.md).
  /// </summary>
  /// <returns>The streams whose base profile changed.</returns>
  public async Task<IReadOnlyList<string>> SetCameraContinuousRecordingAsync(
    string cameraId, bool on, CancellationToken ct)
  {
    await using var command = _dataSource.CreateCommand("""
      WITH target AS (
        SELECT CASE
                 WHEN $2 THEN $3::uuid
                 WHEN COALESCE((SELECT armed FROM camera_states WHERE camera_id = $1), false) THEN $4::uuid
                 ELSE $5::uuid
               END AS profile_id
      )
      UPDATE camera_stream cs
      SET recording_profile_id = (SELECT profile_id FROM target)
      WHERE cs.camera_id = $1
        AND cs.recordable
        AND COALESCE(cs.disabled, false) = false
        AND cs.recording_profile_id IS DISTINCT FROM (SELECT profile_id FROM target)
      RETURNING cs.stream_name;
      """);
    command.Parameters.AddWithValue(cameraId);
    command.Parameters.AddWithValue(on);
    command.Parameters.AddWithValue(BuiltinContinuousProfileId);
    command.Parameters.AddWithValue(BuiltinPrealarmProfileId);
    command.Parameters.AddWithValue(BuiltinOffProfileId);

    var changed = new List<string>();
    await using var reader = await command.ExecuteReaderAsync(ct);
    while (await reader.ReadAsync(ct))
      changed.Add(reader.GetString(0));
    return changed;
  }

  /// <summary>
  /// Whether this profile may be assigned to this stream. A stream that is not available for
  /// recording accepts only a profile that records nothing: switching recording off is always
  /// allowed, switching it on is not. Answering here rather than at the call sites keeps the base
  /// profile and the override under one rule.
  /// Null means the stream or the profile does not exist — a bad request, not a refusal.
  /// </summary>
  public async Task<bool?> IsRecordingProfileAllowedAsync(
    string cameraId, string streamName, string profileId, CancellationToken ct)
  {
    await using var command = _dataSource.CreateCommand("""
      SELECT cs.recordable, rp.mode
      FROM camera_stream cs, recording_profile rp
      WHERE cs.camera_id = $1 AND cs.stream_name = $2 AND rp.id = $3::uuid;
      """);
    command.Parameters.AddWithValue(cameraId);
    command.Parameters.AddWithValue(streamName);
    command.Parameters.AddWithValue(profileId);

    await using var reader = await command.ExecuteReaderAsync(ct);
    if (!await reader.ReadAsync(ct))
      return null;

    return reader.GetBoolean(0) || reader.GetString(1) == "off";
  }

  public async Task SetStreamRecordingProfileAsync(string cameraId, string streamName, string profileId, CancellationToken ct)
  {
    await using var command = _dataSource.CreateCommand("""
      UPDATE camera_stream
      SET recording_profile_id = $3::uuid
      WHERE camera_id = $1 AND stream_name = $2;
      """);
    command.Parameters.AddWithValue(cameraId);
    command.Parameters.AddWithValue(streamName);
    command.Parameters.AddWithValue(profileId);
    await command.ExecuteNonQueryAsync(ct);
  }

  /// Outcome of an alarm activation. The distinction matters far more than it looks: a chatty
  /// motion detector re-fires every second or two, and treating each re-fire as a fresh activation
  /// made every one of them publish a config change and push camera state — a full reload of every
  /// camera on the node, several times a second, to re-apply a recording mode that was already in
  /// force. Only the transition is news; extending a deadline changes nothing anyone downstream
  /// can observe.
  /// Outcome of an alarm activation. The distinction matters far more than it looks: a chatty
  /// motion detector re-fires every second or two, and treating each re-fire as a fresh activation
  /// made every one of them publish a config change and push camera state — a full reload of every
  /// camera on the node, several times a second, to re-apply a recording mode that was already in
  /// force. Only the transition is news; extending a deadline changes nothing anyone downstream
  /// can observe.
  public readonly record struct AlarmOverrideOutcome(bool Started, bool Extended);

  /// <summary>
  /// Puts the camera's eligible streams under an alarm override until <paramref name="until"/>, and
  /// reports which of them were not already under one.
  /// </summary>
  /// <remarks>
  /// One statement, deliberately. Reading the previous state and writing the new one has to be
  /// atomic: AlarmOverrideExpiryService clears expired overrides concurrently, and doing this as
  /// two passes (one for "not yet overridden", one for "extend the existing") let a sweep land in
  /// between — the row matched neither pass, the activation was reported as "nothing happened", and
  /// AlarmEpisodeTracker then suppressed every repeat for the rest of the episode. The alarm was
  /// lost outright.
  ///
  /// Raw SQL because EF cannot express it: the answer depends on each row's value *before* the
  /// update, which needs a data-modifying CTE (see the raw-SQL exception in CLAUDE.md).
  ///
  /// Eligible is a stream available for recording that is either already under an alarm override
  /// (so its deadline is pushed out), or has no manual override and a base profile that is off or
  /// absent. A stream someone is deliberately recording is left alone.
  /// </remarks>
  public async Task<AlarmOverrideOutcome> ActivateAlarmOverrideAsync(string cameraId, string? streamName, DateTime until, CancellationToken ct)
  {
    await using var command = _dataSource.CreateCommand("""
      WITH target AS (
        SELECT cs.stream_name, cs.alarm_override_until AS was_until
        FROM camera_stream cs
        LEFT JOIN recording_profile rp ON rp.id = cs.recording_profile_id
        WHERE cs.camera_id = $1
          AND ($2::text IS NULL OR cs.stream_name = $2)
          -- A stream that is not available for recording is never a candidate: an alarm must not
          -- be a way around the flag.
          AND cs.recordable
          AND (cs.alarm_override_until IS NOT NULL
               OR (cs.override_profile_id IS NULL
                   AND (cs.recording_profile_id IS NULL OR rp.mode = 'off')))
      ),
      applied AS (
        UPDATE camera_stream cs
        SET override_profile_id  = $3,
            -- Never pull a deadline in: a longer window already granted outlives this alarm.
            alarm_override_until = GREATEST(COALESCE(cs.alarm_override_until, $4), $4)
        FROM target t
        WHERE cs.camera_id = $1 AND cs.stream_name = t.stream_name
        RETURNING t.was_until
      )
      SELECT
        count(*) FILTER (WHERE was_until IS NULL)     AS started,
        count(*) FILTER (WHERE was_until IS NOT NULL) AS extended
      FROM applied;
      """);

    command.Parameters.AddWithValue(cameraId);
    command.Parameters.AddWithValue((object?)streamName ?? DBNull.Value);
    command.Parameters.AddWithValue(BuiltinContinuousProfileId);
    command.Parameters.AddWithValue(until);

    await using var reader = await command.ExecuteReaderAsync(ct);
    if (!await reader.ReadAsync(ct))
      return new AlarmOverrideOutcome(false, false);

    return new AlarmOverrideOutcome(reader.GetInt64(0) > 0, reader.GetInt64(1) > 0);
  }

  /// Ends a timed override whose deadline has passed, returning the stream to its base profile.
  ///
  /// The deadline is re-checked in the UPDATE rather than trusted from the caller's earlier read:
  /// between listing an expired row and clearing it the override can be renewed — an alarm re-fires,
  /// or an operator extends the window — and an unconditional clear would then wipe a deadline that
  /// is once again in the future, cutting short recording somebody deliberately asked for.
  public async Task<bool> ClearAlarmOverrideAsync(string cameraId, string streamName, CancellationToken ct)
  {
    await using var db = await dbFactory.CreateDbContextAsync(ct);
    var now = DateTime.UtcNow;
    var rows = await db.CameraStreams
      .Where(cs => cs.CameraId == cameraId && cs.StreamName == streamName
                && cs.AlarmOverrideUntil != null && cs.AlarmOverrideUntil < now)
      .ExecuteUpdateAsync(s => s
        .SetProperty(cs => cs.OverrideProfileId, (Guid?)null)
        .SetProperty(cs => cs.AlarmOverrideUntil, (DateTime?)null),
        ct);
    return rows > 0;
  }

  public async Task<List<(string CameraId, string StreamName)>> ListAlarmOverrideStreamsAsync(CancellationToken ct)
  {
    await using var db = await dbFactory.CreateDbContextAsync(ct);
    var now = DateTime.UtcNow;
    var rows = await db.CameraStreams
      .Where(cs => cs.AlarmOverrideUntil != null && cs.AlarmOverrideUntil < now)
      .Select(cs => new { cs.CameraId, cs.StreamName })
      .ToListAsync(ct);
    return rows.Select(x => (x.CameraId, x.StreamName)).ToList();
  }

  public async Task<bool> RemoveRecordingProfileAsync(string id, CancellationToken ct)
  {
    await using var command = _dataSource.CreateCommand(
      "DELETE FROM recording_profile WHERE id = $1::uuid AND is_builtin = false;");
    command.Parameters.AddWithValue(id);
    return await command.ExecuteNonQueryAsync(ct) > 0;
  }

  private static async Task LoadStreamsAsync(
    NpgsqlConnection connection,
    IReadOnlyList<CameraSetup> cameras,
    CancellationToken ct)
  {
    if (cameras.Count == 0) return;

    var ids = cameras.Select(c => c.CameraId).ToArray();
    var index = cameras.ToDictionary(c => c.CameraId);

    await using var cmd = new NpgsqlCommand("""
      SELECT cs.camera_id, cs.stream_name, cs.profile_token, cs.path, cs.web_rtc,
             rp.id,  rp.name,  rp.mode,  rp.pre_record_seconds,  rp.post_record_seconds,  rp.segment_seconds,  rp.is_override_preset,  rp.is_builtin,
             ov.id,  ov.name,  ov.mode,  ov.pre_record_seconds,  ov.post_record_seconds,  ov.segment_seconds,  ov.is_override_preset,  ov.is_builtin,
             cs.disabled, cs.recordable, cs.declared_width, cs.declared_height
      FROM camera_stream cs
      LEFT JOIN recording_profile rp ON rp.id = cs.recording_profile_id
      LEFT JOIN recording_profile ov ON ov.id = cs.override_profile_id
      WHERE cs.camera_id = ANY($1)
      ORDER BY cs.camera_id, cs.stream_name;
      """, connection);
    cmd.Parameters.AddWithValue(ids);

    await using var reader = await cmd.ExecuteReaderAsync(ct);
    while (await reader.ReadAsync(ct))
    {
      var camId = reader.GetString(0);
      if (!index.TryGetValue(camId, out var camera)) continue;

      var stream = new CameraStream
      {
        StreamName   = reader.GetString(1),
        ProfileToken = reader.GetString(2),
        Path         = reader.GetString(3),
        WebRtc       = reader.GetBoolean(4),
      };

      if (!reader.IsDBNull(5))
        stream.RecordingProfile = ReadRecordingProfileFromRow(reader, offset: 5);

      if (!reader.IsDBNull(13))
        stream.OverrideProfile = ReadRecordingProfileFromRow(reader, offset: 13);

      if (!reader.IsDBNull(21) && reader.GetBoolean(21))
        stream.Disabled = true;

      stream.Recordable = reader.GetBoolean(22);
      stream.DeclaredWidth = reader.GetInt32(23);
      stream.DeclaredHeight = reader.GetInt32(24);

      camera.Streams.Add(stream);
    }
  }

  private static RecordingProfile ReadRecordingProfile(NpgsqlDataReader reader) =>
    ReadRecordingProfileFromRow(reader, offset: 0);

  private static RecordingProfile ReadRecordingProfileFromRow(NpgsqlDataReader reader, int offset) =>
    new()
    {
      Id                = reader.GetGuid(offset).ToString(),
      Name              = reader.GetString(offset + 1),
      Mode              = ParseRecordingMode(reader.GetString(offset + 2)),
      PreRecordSeconds  = (uint)reader.GetInt32(offset + 3),
      PostRecordSeconds = (uint)reader.GetInt32(offset + 4),
      SegmentSeconds    = (uint)reader.GetInt32(offset + 5),
      IsOverridePreset  = reader.GetBoolean(offset + 6),
      IsBuiltin         = reader.GetBoolean(offset + 7),
    };

  private static string RecordingModeToDb(RecordingMode mode) => mode switch
  {
    RecordingMode.RecordingContinuous => "continuous",
    RecordingMode.RecordingPrealarm   => "prealarm",
    _                                 => "off",
  };

  private static RecordingMode ParseRecordingMode(string raw) =>
    raw.Trim().ToLowerInvariant() switch
    {
      "continuous" => RecordingMode.RecordingContinuous,
      "prealarm"   => RecordingMode.RecordingPrealarm,
      _            => RecordingMode.RecordingOff,
    };

  private static CameraSetup ReadCameraSetup(NpgsqlDataReader reader)
  {
    var setup = new CameraSetup
    {
      CameraId = reader.GetString(0),
      ServerId = reader.GetString(1),
    };

    if (!reader.IsDBNull(2) || !reader.IsDBNull(3))
      setup.Cred = new Credentials
      {
        User = reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
        Pass = reader.IsDBNull(3) ? string.Empty : reader.GetString(3)
      };

    if (!reader.IsDBNull(4))
      setup.OnvifConn = new OnvifConnection
      {
        Ip    = reader.GetString(4),
        Port  = reader.IsDBNull(5) ? 0 : reader.GetInt32(5),
        Xaddr = reader.IsDBNull(6) ? string.Empty : reader.GetString(6)
      };

    if (!reader.IsDBNull(7))
      setup.MediaMtxServerId = reader.GetGuid(7).ToString();

    setup.OnvifEventsEnabled = reader.GetBoolean(8);
    setup.HasOnvifFilters    = reader.GetBoolean(9);
    setup.OnvifManaged       = reader.GetBoolean(10);
    if (!reader.IsDBNull(11) && reader.GetBoolean(11))
      setup.Disabled = true;

    setup.OnvifPollTimeoutSeconds = reader.GetInt32(12);
    setup.OnvifMessageLimit       = reader.GetInt32(13);
    setup.Name                    = reader.GetString(14);

    // Left unset when no media router is assigned — the browser then falls back to its default
    // ICE config instead of reading "no router" as "no STUN".
    if (!reader.IsDBNull(15))
      setup.EffectiveWebrtcStunUrl = reader.GetString(15);

    setup.EdgeBackfillStream = reader.IsDBNull(16) ? string.Empty : reader.GetString(16);
    setup.RoiTranscode = !reader.IsDBNull(17) && reader.GetBoolean(17);
    setup.RoiDelayHours = reader.IsDBNull(18) ? 0 : reader.GetDouble(18);
    setup.RoiMinCoverage = reader.IsDBNull(19) ? 0 : reader.GetDouble(19);
    setup.RoiMaxCoverage = reader.IsDBNull(20) ? 1 : reader.GetDouble(20);
    setup.OnvifMetadataDisabled = !reader.IsDBNull(21) && reader.GetBoolean(21);
    setup.HasTelemetry = !reader.IsDBNull(22) && reader.GetBoolean(22);

    return setup;
  }

  // ── MediaMTX server management ───────────────────────────────────────────

  public async Task<List<MediaMtxServer>> ListMediaMtxServersAsync(CancellationToken ct)
  {
    await using var command = _dataSource.CreateCommand("""
      SELECT id, name, host, webrtc_host, rtsp_port, webrtc_port, api_port, stun_url, server_id,
             publish_user, publish_pass, read_user, read_pass, transcode_hevc
      FROM media_mtx_server ORDER BY name;
      """);

    var result = new List<MediaMtxServer>();
    await using var reader = await command.ExecuteReaderAsync(ct);
    while (await reader.ReadAsync(ct))
      result.Add(ReadMediaMtxServer(reader));
    return result;
  }

  public async Task UpsertMediaMtxServerAsync(MediaMtxServer server, CancellationToken ct)
  {
    await using var command = _dataSource.CreateCommand("""
      INSERT INTO media_mtx_server (
        id, name, host, webrtc_host, rtsp_port, webrtc_port, api_port, stun_url, server_id,
        publish_user, publish_pass, read_user, read_pass, transcode_hevc)
      VALUES (
        COALESCE(NULLIF($1, '')::uuid, gen_random_uuid()), $2, $3, $4, $5, $6, $7, $8,
        NULLIF($9, ''), $10, $11, $12, $13, $14)
      ON CONFLICT (id) DO UPDATE SET
        name        = EXCLUDED.name,
        host        = EXCLUDED.host,
        webrtc_host = EXCLUDED.webrtc_host,
        rtsp_port   = EXCLUDED.rtsp_port,
        webrtc_port = EXCLUDED.webrtc_port,
        api_port    = EXCLUDED.api_port,
        stun_url    = EXCLUDED.stun_url,
        server_id   = EXCLUDED.server_id,
        publish_user = EXCLUDED.publish_user,
        publish_pass = EXCLUDED.publish_pass,
        read_user    = EXCLUDED.read_user,
        read_pass    = EXCLUDED.read_pass,
        transcode_hevc = EXCLUDED.transcode_hevc
      RETURNING id;
      """);
    command.Parameters.AddWithValue(server.Id ?? string.Empty);
    command.Parameters.AddWithValue(server.Name);
    command.Parameters.AddWithValue(server.Host);
    command.Parameters.AddWithValue(server.WebrtcHost);
    command.Parameters.AddWithValue(server.RtspPort);
    command.Parameters.AddWithValue(server.WebrtcPort);
    command.Parameters.AddWithValue(server.ApiPort);
    command.Parameters.AddWithValue(server.StunUrl ?? string.Empty);
    command.Parameters.AddWithValue(server.ServerId ?? string.Empty);
    command.Parameters.AddWithValue(server.PublishUser?.Trim() ?? string.Empty);
    command.Parameters.AddWithValue(server.PublishPass ?? string.Empty);
    command.Parameters.AddWithValue(server.ReadUser?.Trim() ?? string.Empty);
    command.Parameters.AddWithValue(server.ReadPass ?? string.Empty);
    command.Parameters.AddWithValue(server.TranscodeHevc);
    var id = await command.ExecuteScalarAsync(ct);
    server.Id = id?.ToString() ?? server.Id;
  }

  public async Task<bool> RemoveMediaMtxServerAsync(string id, CancellationToken ct)
  {
    await using var command = _dataSource.CreateCommand(
      "DELETE FROM media_mtx_server WHERE id = $1::uuid;");
    command.Parameters.AddWithValue(id);
    return await command.ExecuteNonQueryAsync(ct) > 0;
  }

  /// <summary>
  /// The MediaMTX a camera publishes to, assigning one on first use.
  ///
  /// A camera is only ever given a server that belongs to its own node or is shared (server_id
  /// NULL): video_srv pushes the stream to whatever is assigned here, so picking across nodes
  /// would carry video over the link between them. An assignment already made is left alone — a
  /// camera that later moves nodes is re-pointed by an administrator, not silently on a read — but
  /// the mismatch is logged so it does not stay invisible.
  /// <c>Assigned</c> is true when this call wrote the assignment, which the caller must announce:
  /// video_srv builds the camera's push target from it and learns of it only through an event.
  /// </summary>
  public async Task<(MediaMtxServer? Server, bool Assigned)> GetOrAssignMediaMtxServerAsync(string cameraId, CancellationToken ct)
  {
    await using var connection = await _dataSource.OpenConnectionAsync(ct);

    string? cameraServerId = null;

    // One query for both questions: which node the camera is on, and whether it already has a
    // server. LEFT JOIN so an unassigned camera still reports its node for the pick below.
    await using (var cmd = new NpgsqlCommand("""
      SELECT cs.server_id,
             m.id, m.name, m.host, m.webrtc_host, m.rtsp_port, m.webrtc_port, m.api_port, m.stun_url,
             m.server_id, m.publish_user, m.publish_pass, m.read_user, m.read_pass, m.transcode_hevc
      FROM camera_setup cs
      LEFT JOIN media_mtx_server m ON m.id = cs.media_mtx_server_id
      WHERE cs.camera_id = $1;
      """, connection))
    {
      cmd.Parameters.AddWithValue(cameraId);
      await using var reader = await cmd.ExecuteReaderAsync(ct);
      if (!await reader.ReadAsync(ct))
        return (null, false);

      cameraServerId = reader.IsDBNull(0) ? null : reader.GetString(0);
      if (!reader.IsDBNull(1))
      {
        var assigned = ReadMediaMtxServer(reader, offset: 1);
        if (!string.IsNullOrEmpty(assigned.ServerId)
          && !string.Equals(assigned.ServerId, cameraServerId, StringComparison.OrdinalIgnoreCase))
        {
          logger.LogWarning(
            "Camera '{CameraId}' on node '{CameraNode}' publishes to MediaMTX '{Name}' of node "
            + "'{MtxNode}' — its stream crosses the link between them; reassign it",
            cameraId, cameraServerId, assigned.Name, assigned.ServerId);
        }
        return (assigned, false);
      }
    }

    // Pick among the servers that can serve this node: its own, or a shared one.
    await using var tx = await connection.BeginTransactionAsync(ct);
    MediaMtxServer? picked = null;

    await using (var pickCmd = new NpgsqlCommand("""
      SELECT id, name, host, webrtc_host, rtsp_port, webrtc_port, api_port, stun_url, server_id,
             publish_user, publish_pass, read_user, read_pass, transcode_hevc
      FROM media_mtx_server
      WHERE server_id IS NULL OR server_id = $1
      ORDER BY random() LIMIT 1;
      """, connection, tx))
    {
      pickCmd.Parameters.AddWithValue((object?)cameraServerId ?? DBNull.Value);
      await using var reader = await pickCmd.ExecuteReaderAsync(ct);
      if (await reader.ReadAsync(ct))
        picked = ReadMediaMtxServer(reader);
    }

    if (picked is null)
    {
      // Deliberately not falling back to another node's server: that would look like it worked
      // while quietly sending the camera's video across the link.
      logger.LogWarning(
        "No MediaMTX server available for camera '{CameraId}' on node '{CameraNode}' — configure one "
        + "for that node, or leave a server's node empty to share it",
        cameraId, cameraServerId);
      return (null, false);
    }

    await using (var assignCmd = new NpgsqlCommand("""
      UPDATE camera_setup SET media_mtx_server_id = $2::uuid WHERE camera_id = $1;
      """, connection, tx))
    {
      assignCmd.Parameters.AddWithValue(cameraId);
      assignCmd.Parameters.AddWithValue(picked.Id);
      await assignCmd.ExecuteNonQueryAsync(ct);
    }

    await tx.CommitAsync(ct);
    return (picked, true);
  }

  /// offset lets the same mapper read the server columns when they sit behind other columns in a
  /// joined row.
  private static MediaMtxServer ReadMediaMtxServer(NpgsqlDataReader reader, int offset = 0) =>
    new()
    {
      Id         = reader.GetGuid(offset).ToString(),
      Name       = reader.GetString(offset + 1),
      Host       = reader.GetString(offset + 2),
      WebrtcHost = reader.GetString(offset + 3),
      RtspPort   = reader.GetInt32(offset + 4),
      WebrtcPort = reader.GetInt32(offset + 5),
      ApiPort    = reader.GetInt32(offset + 6),
      StunUrl    = reader.GetString(offset + 7),
      ServerId   = reader.IsDBNull(offset + 8) ? string.Empty : reader.GetString(offset + 8),
      PublishUser = reader.GetString(offset + 9),
      PublishPass = reader.GetString(offset + 10),
      ReadUser    = reader.GetString(offset + 11),
      ReadPass    = reader.GetString(offset + 12),
      TranscodeHevc = reader.GetBoolean(offset + 13),
    };

  // ── Monitor management ────────────────────────────────────────────────────

  public const string MonitorKindWall = "wall";
  public const string MonitorKindInvestigation = "investigation";

  /// <summary>
  /// Walls are visible to everyone; investigation boards only to their owner. This filter is the
  /// access boundary for private boards, so it lives in SQL rather than in the calling service.
  /// </summary>
  public async Task<List<MonitorSetup>> ListMonitorsAsync(string owner, CancellationToken ct)
  {
    await using var connection = await _dataSource.OpenConnectionAsync(ct);

    var monitors = new Dictionary<Guid, MonitorSetup>();
    var order    = new List<Guid>();

    await using (var cmd = new NpgsqlCommand("""
      SELECT id, name, layout, kind, owner FROM monitor
      WHERE kind = 'wall' OR owner = $1
      ORDER BY kind, name;
      """, connection))
    {
      cmd.Parameters.AddWithValue(owner);
      await using var reader = await cmd.ExecuteReaderAsync(ct);
      while (await reader.ReadAsync(ct))
      {
        var id = reader.GetGuid(0);
        monitors[id] = new MonitorSetup
        {
          MonitorId = id.ToString(),
          Name      = reader.GetString(1),
          Layout    = reader.GetString(2),
          Kind      = reader.GetString(3),
          Owner     = reader.GetString(4),
        };
        order.Add(id);
      }
    }

    if (monitors.Count == 0)
      return [];

    var idArray = order.ToArray();

    await using (var cmd = new NpgsqlCommand("""
      SELECT monitor_id, camera_id FROM monitor_camera
      WHERE monitor_id = ANY($1)
      ORDER BY monitor_id, position;
      """, connection))
    {
      cmd.Parameters.Add(new NpgsqlParameter { Value = idArray, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Uuid });
      await using var reader = await cmd.ExecuteReaderAsync(ct);
      while (await reader.ReadAsync(ct))
      {
        if (monitors.TryGetValue(reader.GetGuid(0), out var m))
          m.CameraIds.Add(reader.GetString(1));
      }
    }

    // The pinned stream is this caller's own; the camera in the slot is everyone's. Joined here so
    // a wall shared by several operators still answers each of them with their own pin.
    await using (var cmd = new NpgsqlCommand("""
      SELECT ms.monitor_id, ms.slot_index, ms.camera_id, COALESCE(p.stream_name, '')
      FROM monitor_slot ms
      LEFT JOIN monitor_slot_pref p
        ON p.monitor_id = ms.monitor_id AND p.slot_index = ms.slot_index AND p.owner = $2
      WHERE ms.monitor_id = ANY($1);
      """, connection))
    {
      cmd.Parameters.Add(new NpgsqlParameter { Value = idArray, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Uuid });
      cmd.Parameters.AddWithValue(owner);
      await using var reader = await cmd.ExecuteReaderAsync(ct);
      while (await reader.ReadAsync(ct))
      {
        if (monitors.TryGetValue(reader.GetGuid(0), out var m))
          m.Slots.Add(new MonitorSlot
          {
            SlotIndex  = reader.GetInt32(1),
            CameraId   = reader.GetString(2),
            StreamName = reader.GetString(3),
          });
      }
    }

    return order.Select(id => monitors[id]).ToList();
  }

  /// <summary>
  /// Pins a slot to one stream for this operator, or releases it back to automatic selection when
  /// <paramref name="streamName"/> is empty. Releasing deletes the row rather than storing an empty
  /// one: automatic is the default, and a table of rows saying "default" is a table nobody can read.
  /// </summary>
  public async Task SetMonitorSlotStreamAsync(
    string owner, Guid monitorId, int slotIndex, string streamName, CancellationToken ct)
  {
    if (string.IsNullOrEmpty(streamName))
    {
      await using var deleteCmd = _dataSource.CreateCommand("""
        DELETE FROM monitor_slot_pref
        WHERE owner = $1 AND monitor_id = $2 AND slot_index = $3;
        """);
      deleteCmd.Parameters.AddWithValue(owner);
      deleteCmd.Parameters.AddWithValue(monitorId);
      deleteCmd.Parameters.AddWithValue(slotIndex);
      await deleteCmd.ExecuteNonQueryAsync(ct);
      return;
    }

    await using var command = _dataSource.CreateCommand("""
      INSERT INTO monitor_slot_pref (owner, monitor_id, slot_index, stream_name)
      VALUES ($1, $2, $3, $4)
      ON CONFLICT (owner, monitor_id, slot_index) DO UPDATE SET stream_name = EXCLUDED.stream_name;
      """);
    command.Parameters.AddWithValue(owner);
    command.Parameters.AddWithValue(monitorId);
    command.Parameters.AddWithValue(slotIndex);
    command.Parameters.AddWithValue(streamName);
    await command.ExecuteNonQueryAsync(ct);
  }

  public async Task<int> CountInvestigationBoardsAsync(string owner, CancellationToken ct)
  {
    await using var command = _dataSource.CreateCommand(
      "SELECT COUNT(*) FROM monitor WHERE kind = 'investigation' AND owner = $1;");
    command.Parameters.AddWithValue(owner);
    return (int)(long)(await command.ExecuteScalarAsync(ct) ?? 0L);
  }

  /// <summary>
  /// Returns null when the monitor exists but belongs to somebody else. kind and owner are set on
  /// insert only — an update never reassigns them, so ownership cannot be taken over by upserting
  /// over an existing id.
  /// </summary>
  public async Task<MonitorSetup?> UpsertMonitorAsync(MonitorSetup request, CancellationToken ct)
  {
    await using var command = _dataSource.CreateCommand("""
      INSERT INTO monitor (id, name, layout, kind, owner)
      VALUES (COALESCE(NULLIF($1, '')::uuid, gen_random_uuid()), $2, $3, $4, $5)
      ON CONFLICT (id) DO UPDATE SET
        name   = EXCLUDED.name,
        layout = EXCLUDED.layout
      WHERE monitor.kind = 'wall' OR monitor.owner = $5
      RETURNING id;
      """);
    command.Parameters.AddWithValue(request.MonitorId ?? string.Empty);
    command.Parameters.AddWithValue(request.Name);
    command.Parameters.AddWithValue(request.Layout);
    command.Parameters.AddWithValue(string.IsNullOrEmpty(request.Kind) ? MonitorKindWall : request.Kind);
    command.Parameters.AddWithValue(request.Owner ?? string.Empty);
    var id = await command.ExecuteScalarAsync(ct);
    // No row back means the ON CONFLICT update was filtered out — the id exists and is not ours.
    if (id is null)
      return null;

    request.MonitorId = id.ToString() ?? request.MonitorId;
    return request;
  }

  public async Task<bool> RemoveMonitorAsync(string monitorId, string owner, CancellationToken ct)
  {
    await using var command = _dataSource.CreateCommand(
      "DELETE FROM monitor WHERE id = $1::uuid AND (kind = 'wall' OR owner = $2);");
    command.Parameters.AddWithValue(monitorId);
    command.Parameters.AddWithValue(owner);
    return await command.ExecuteNonQueryAsync(ct) > 0;
  }

  public async Task<bool> SetMonitorCamerasAsync(string monitorId, IEnumerable<string> cameraIds, string owner, CancellationToken ct)
  {
    await using var connection = await _dataSource.OpenConnectionAsync(ct);
    await using var tx = await connection.BeginTransactionAsync(ct);

    if (!await IsWritableAsync(connection, tx, monitorId, owner, ct))
      return false;

    await using (var del = new NpgsqlCommand(
      "DELETE FROM monitor_camera WHERE monitor_id = $1::uuid;", connection, tx))
    {
      del.Parameters.AddWithValue(monitorId);
      await del.ExecuteNonQueryAsync(ct);
    }

    var pool = cameraIds as IList<string> ?? cameraIds.ToList();

    // Only slots whose camera left the pool are dropped. Clearing every slot here would empty the
    // whole grid each time one camera is added, which is exactly what adding a camera to an
    // investigation board does.
    await using (var delSlots = new NpgsqlCommand(
      "DELETE FROM monitor_slot WHERE monitor_id = $1::uuid AND camera_id <> ALL($2);", connection, tx))
    {
      delSlots.Parameters.AddWithValue(monitorId);
      delSlots.Parameters.Add(new NpgsqlParameter { Value = pool.ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text });
      await delSlots.ExecuteNonQueryAsync(ct);
    }

    int pos = 0;
    foreach (var cameraId in pool)
    {
      await using var ins = new NpgsqlCommand("""
        INSERT INTO monitor_camera (monitor_id, camera_id, position)
        VALUES ($1::uuid, $2, $3);
        """, connection, tx);
      ins.Parameters.AddWithValue(monitorId);
      ins.Parameters.AddWithValue(cameraId);
      ins.Parameters.AddWithValue(pos++);
      await ins.ExecuteNonQueryAsync(ct);
    }

    await tx.CommitAsync(ct);
    return true;
  }

  public async Task<bool> SetMonitorSlotAsync(string monitorId, int slotIndex, string cameraId, string owner, CancellationToken ct)
  {
    // The ownership check is part of the same statement rather than a preceding SELECT: a slot
    // change is a single write and must not be racing a concurrent delete of the board.
    await using var command = _dataSource.CreateCommand("""
      INSERT INTO monitor_slot (monitor_id, slot_index, camera_id)
      SELECT $1::uuid, $2, $3
      WHERE EXISTS (
        SELECT 1 FROM monitor WHERE id = $1::uuid AND (kind = 'wall' OR owner = $4))
      ON CONFLICT (monitor_id, slot_index) DO UPDATE SET camera_id = EXCLUDED.camera_id;
      """);
    command.Parameters.AddWithValue(monitorId);
    command.Parameters.AddWithValue(slotIndex);
    command.Parameters.AddWithValue(cameraId);
    command.Parameters.AddWithValue(owner);
    return await command.ExecuteNonQueryAsync(ct) > 0;
  }

  public async Task<bool> ClearMonitorSlotAsync(string monitorId, int slotIndex, string owner, CancellationToken ct)
  {
    await using var command = _dataSource.CreateCommand("""
      DELETE FROM monitor_slot
      WHERE monitor_id = $1::uuid AND slot_index = $2
        AND EXISTS (
          SELECT 1 FROM monitor WHERE id = $1::uuid AND (kind = 'wall' OR owner = $3));
      """);
    command.Parameters.AddWithValue(monitorId);
    command.Parameters.AddWithValue(slotIndex);
    command.Parameters.AddWithValue(owner);
    return await command.ExecuteNonQueryAsync(ct) > 0;
  }

  /// <summary>
  /// Copies a board to another user, who becomes its owner — sharing an investigation hands over
  /// an independent board rather than granting access to the original.
  /// Returns null when the source is not the caller's to copy.
  /// </summary>
  public async Task<MonitorSetup?> CopyMonitorAsync(
    string monitorId,
    string owner,
    string targetOwner,
    CancellationToken ct)
  {
    await using var connection = await _dataSource.OpenConnectionAsync(ct);
    await using var tx = await connection.BeginTransactionAsync(ct);

    string sourceName;
    string sourceLayout;
    await using (var cmd = new NpgsqlCommand(
      "SELECT name, layout FROM monitor WHERE id = $1::uuid AND owner = $2;", connection, tx))
    {
      cmd.Parameters.AddWithValue(monitorId);
      cmd.Parameters.AddWithValue(owner);
      await using var reader = await cmd.ExecuteReaderAsync(ct);
      if (!await reader.ReadAsync(ct))
        return null;
      sourceName   = reader.GetString(0);
      sourceLayout = reader.GetString(1);
    }

    var baseName = string.IsNullOrEmpty(owner) ? sourceName : $"{sourceName} (from {owner})";
    var name = await FreeMonitorNameAsync(connection, tx, baseName, targetOwner, ct);

    Guid newId;
    await using (var cmd = new NpgsqlCommand("""
      INSERT INTO monitor (name, layout, kind, owner)
      VALUES ($1, $2, 'investigation', $3)
      RETURNING id;
      """, connection, tx))
    {
      cmd.Parameters.AddWithValue(name);
      cmd.Parameters.AddWithValue(sourceLayout);
      cmd.Parameters.AddWithValue(targetOwner);
      newId = (Guid)(await cmd.ExecuteScalarAsync(ct))!;
    }

    await using (var cmd = new NpgsqlCommand("""
      INSERT INTO monitor_camera (monitor_id, position, camera_id)
      SELECT $1::uuid, position, camera_id FROM monitor_camera WHERE monitor_id = $2::uuid;
      """, connection, tx))
    {
      cmd.Parameters.AddWithValue(newId);
      cmd.Parameters.AddWithValue(monitorId);
      await cmd.ExecuteNonQueryAsync(ct);
    }

    await using (var cmd = new NpgsqlCommand("""
      INSERT INTO monitor_slot (monitor_id, slot_index, camera_id)
      SELECT $1::uuid, slot_index, camera_id FROM monitor_slot WHERE monitor_id = $2::uuid;
      """, connection, tx))
    {
      cmd.Parameters.AddWithValue(newId);
      cmd.Parameters.AddWithValue(monitorId);
      await cmd.ExecuteNonQueryAsync(ct);
    }

    await tx.CommitAsync(ct);

    var copy = new MonitorSetup
    {
      MonitorId = newId.ToString(),
      Name      = name,
      Layout    = sourceLayout,
      Kind      = MonitorKindInvestigation,
      Owner     = targetOwner,
    };
    return copy;
  }

  private static async Task<bool> IsWritableAsync(
    NpgsqlConnection connection,
    NpgsqlTransaction tx,
    string monitorId,
    string owner,
    CancellationToken ct)
  {
    await using var cmd = new NpgsqlCommand(
      "SELECT 1 FROM monitor WHERE id = $1::uuid AND (kind = 'wall' OR owner = $2);", connection, tx);
    cmd.Parameters.AddWithValue(monitorId);
    cmd.Parameters.AddWithValue(owner);
    return await cmd.ExecuteScalarAsync(ct) is not null;
  }

  // Names are unique per (name, kind, owner); a copy sent twice must not fail on the second send.
  private static async Task<string> FreeMonitorNameAsync(
    NpgsqlConnection connection,
    NpgsqlTransaction tx,
    string baseName,
    string owner,
    CancellationToken ct)
  {
    for (var attempt = 1; ; attempt++)
    {
      var candidate = attempt == 1 ? baseName : $"{baseName} {attempt}";
      await using var cmd = new NpgsqlCommand(
        "SELECT 1 FROM monitor WHERE name = $1 AND kind = 'investigation' AND owner = $2;",
        connection, tx);
      cmd.Parameters.AddWithValue(candidate);
      cmd.Parameters.AddWithValue(owner);
      if (await cmd.ExecuteScalarAsync(ct) is null)
        return candidate;
    }
  }

  // ── ONVIF alarm filters ───────────────────────────────────────────────────

  public async Task<List<OnvifAlarmFilter>> ListOnvifAlarmFiltersAsync(string cameraId, CancellationToken ct)
  {
    await using var db = await dbFactory.CreateDbContextAsync(ct);
    var rows = await db.OnvifAlarmFilters
      .Where(f => f.CameraId == cameraId)
      .OrderBy(f => f.Id)
      .ToListAsync(ct);
    return rows.Select(ToProto).ToList();
  }

  public async Task<OnvifAlarmFilter> UpsertOnvifAlarmFilterAsync(OnvifAlarmFilter filter, CancellationToken ct)
  {
    await using var db = await dbFactory.CreateDbContextAsync(ct);

    DbOnvifAlarmFilter entity;
    if (Guid.TryParse(filter.Id, out var existingId))
    {
      entity = await db.OnvifAlarmFilters.FindAsync([existingId], ct)
               ?? db.OnvifAlarmFilters.Add(new DbOnvifAlarmFilter { Id = existingId, CameraId = filter.CameraId }).Entity;
    }
    else
    {
      entity = db.OnvifAlarmFilters.Add(new DbOnvifAlarmFilter { Id = Guid.NewGuid(), CameraId = filter.CameraId }).Entity;
    }

    entity.FilterText     = filter.FilterText;
    entity.TimeoutSeconds = filter.TimeoutSeconds;
    entity.TextFilter     = filter.TextFilter;

    await db.SaveChangesAsync(ct);
    return ToProto(entity);
  }

  public async Task<(bool Removed, string? CameraId)> RemoveOnvifAlarmFilterAsync(string id, CancellationToken ct)
  {
    if (!Guid.TryParse(id, out var guid)) return (false, null);
    await using var db = await dbFactory.CreateDbContextAsync(ct);
    var filter = await db.OnvifAlarmFilters.FindAsync([guid], ct);
    if (filter == null) return (false, null);
    db.OnvifAlarmFilters.Remove(filter);
    await db.SaveChangesAsync(ct);
    return (true, filter.CameraId);
  }

  private static OnvifAlarmFilter ToProto(DbOnvifAlarmFilter row) =>
    new()
    {
      Id             = row.Id.ToString(),
      CameraId       = row.CameraId,
      FilterText     = row.FilterText,
      TimeoutSeconds = row.TimeoutSeconds,
      TextFilter     = row.TextFilter,
    };

  public async Task SubscribeOnvifEventsAsync(string cameraId, CancellationToken ct)
  {
    await using var cmd = _dataSource.CreateCommand("""
      INSERT INTO onvif_event_subscription (camera_id, subscribed) VALUES ($1, true)
      ON CONFLICT (camera_id) DO NOTHING;
      """);
    cmd.Parameters.AddWithValue(cameraId);
    await cmd.ExecuteNonQueryAsync(ct);
  }

  public async Task UnsubscribeOnvifEventsAsync(string cameraId, CancellationToken ct)
  {
    await using var cmd = _dataSource.CreateCommand(
      "DELETE FROM onvif_event_subscription WHERE camera_id = $1;");
    cmd.Parameters.AddWithValue(cameraId);
    await cmd.ExecuteNonQueryAsync(ct);
  }

  public async Task SetOnvifSubscribedAsync(string cameraId, bool subscribed, CancellationToken ct)
  {
    await using var cmd = _dataSource.CreateCommand(
      "UPDATE onvif_event_subscription SET subscribed = $2 WHERE camera_id = $1;");
    cmd.Parameters.AddWithValue(cameraId);
    cmd.Parameters.AddWithValue(subscribed);
    await cmd.ExecuteNonQueryAsync(ct);
  }

  public async Task<List<(string CameraId, bool Subscribed, int PollTimeoutSeconds, int MessageLimit)>> ListOnvifSubscriptionsAsync(CancellationToken ct)
  {
    await using var cmd = _dataSource.CreateCommand(
      "SELECT camera_id, subscribed, poll_timeout_seconds, message_limit FROM onvif_event_subscription ORDER BY camera_id;");
    var result = new List<(string, bool, int, int)>();
    await using var reader = await cmd.ExecuteReaderAsync(ct);
    while (await reader.ReadAsync(ct))
      result.Add((reader.GetString(0), reader.GetBoolean(1), reader.GetInt32(2), reader.GetInt32(3)));
    return result;
  }

  public async Task SetOnvifPollConfigAsync(string cameraId, int pollTimeoutSeconds, int messageLimit, CancellationToken ct)
  {
    await using var cmd = _dataSource.CreateCommand(
      "UPDATE onvif_event_subscription SET poll_timeout_seconds = $2, message_limit = $3 WHERE camera_id = $1;");
    cmd.Parameters.AddWithValue(cameraId);
    cmd.Parameters.AddWithValue(pollTimeoutSeconds);
    cmd.Parameters.AddWithValue(messageLimit);
    await cmd.ExecuteNonQueryAsync(ct);
  }

  public async Task<bool> GetCameraArmedAsync(string cameraId, CancellationToken ct)
  {
    await using var cmd = _dataSource.CreateCommand(
      "SELECT armed FROM camera_states WHERE camera_id = $1;");
    cmd.Parameters.AddWithValue(cameraId);
    var result = await cmd.ExecuteScalarAsync(ct);
    return result is bool b && b;
  }

  public async Task SetCameraArmedAsync(string cameraId, bool armed, CancellationToken ct)
  {
    await using var cmd = _dataSource.CreateCommand("""
      INSERT INTO camera_states (camera_id, armed) VALUES ($1, $2)
      ON CONFLICT (camera_id) DO UPDATE SET armed = EXCLUDED.armed;
      """);
    cmd.Parameters.AddWithValue(cameraId);
    cmd.Parameters.AddWithValue(armed);
    await cmd.ExecuteNonQueryAsync(ct);
  }

  // ── Analytics watches (1:1 per camera, mirrors onvif_event_subscription) ─

  public async Task<List<AnalyticsWatch>> ListAnalyticsWatchesAsync(string? cameraId, CancellationToken ct)
  {
    await using var db = await dbFactory.CreateDbContextAsync(ct);
    var query = db.AnalyticsWatches.AsQueryable();
    if (!string.IsNullOrWhiteSpace(cameraId))
      query = query.Where(w => w.CameraId == cameraId);

    var rows = await query.OrderBy(w => w.CameraId).ToListAsync(ct);
    // Armed for every row in one query. Asking per row turned a list of N watches into N+1 round
    // trips, which is what GetCameraAnalyticsSourcesAsync below deliberately avoids as well.
    var armed = await ArmedByCameraAsync(db, rows.Select(r => r.CameraId), ct);
    return rows.Select(r => ToAnalyticsWatchProto(r, armed.GetValueOrDefault(r.CameraId))).ToList();
  }

  /// Armed flags for these cameras, keyed by camera id. Cameras with no camera_states row are
  /// absent -- callers treat that as not armed.
  private static async Task<Dictionary<string, bool>> ArmedByCameraAsync(
    VmsCfgDbContext db, IEnumerable<string> cameraIds, CancellationToken ct)
  {
    var ids = cameraIds.Distinct().ToList();
    if (ids.Count == 0) return [];
    return await db.CameraStates.Where(c => ids.Contains(c.CameraId))
      .ToDictionaryAsync(c => c.CameraId, c => c.Armed, ct);
  }

  public async Task<AnalyticsWatch> UpsertAnalyticsWatchAsync(AnalyticsWatch watch, CancellationToken ct)
  {
    await using var db = await dbFactory.CreateDbContextAsync(ct);

    var entity = await db.AnalyticsWatches.FindAsync([watch.CameraId], ct)
                 ?? db.AnalyticsWatches.Add(new DbAnalyticsWatch { CameraId = watch.CameraId }).Entity;

    entity.StreamName = watch.StreamName;
    entity.Enabled = watch.Enabled;
    entity.Classes = string.Join(',', watch.Classes);
    entity.MinConfidence = watch.MinConfidence;
    entity.SampleFps = (int)watch.SampleFps;
    entity.AttachDebugCrops = watch.AttachDebugCrops;
    entity.StoreEmbeddings = watch.StoreEmbeddings;
    // 0 means "not supplied" (proto3 scalars have no presence) — keep whatever is stored rather
    // than silently collapsing an existing timeout to zero.
    if (watch.TimeoutSeconds > 0) entity.TimeoutSeconds = (int)watch.TimeoutSeconds;

    await db.SaveChangesAsync(ct);
    return await ToAnalyticsWatchProtoAsync(entity, ct);
  }

  public async Task<bool> RemoveAnalyticsWatchAsync(string cameraId, CancellationToken ct)
  {
    await using var db = await dbFactory.CreateDbContextAsync(ct);
    var watch = await db.AnalyticsWatches.FindAsync([cameraId], ct);
    if (watch == null) return false;
    db.AnalyticsWatches.Remove(watch);
    await db.SaveChangesAsync(ct);
    return true;
  }

  // ── Analytics sources, aggregated per camera ────────────────────────────

  /// Every analytics source a camera has, in one pass. Five queries regardless of how many cameras
  /// are asked for -- the four tables that hold the configuration plus the filter counts -- because
  /// the caller renders all of them at once and per-camera round trips are what this replaces.
  ///
  /// Read-only on purpose: writes stay on the per-source RPCs, each of which carries its own side
  /// effects (cache invalidation, the camera-state-changed topic, the analytics control plane).
  ///
  /// The camera set comes from camera_setup, so a camera with no rows in any of the other tables
  /// still appears -- with every source off, which is the honest answer and a different one from
  /// "no such camera".
  public async Task<List<CameraAnalyticsSources>> GetCameraAnalyticsSourcesAsync(
    IReadOnlyCollection<string> cameraIds, CancellationToken ct)
  {
    await using var db = await dbFactory.CreateDbContextAsync(ct);

    var wanted = cameraIds.Distinct().ToList();
    var setupQuery = db.CameraSetups.AsQueryable();
    if (wanted.Count > 0) setupQuery = setupQuery.Where(c => wanted.Contains(c.CameraId));
    var setups = await setupQuery.OrderBy(c => c.CameraId).ToListAsync(ct);
    if (setups.Count == 0) return [];

    var ids = setups.Select(c => c.CameraId).ToList();

    var watches = await db.AnalyticsWatches.Where(w => ids.Contains(w.CameraId))
      .ToDictionaryAsync(w => w.CameraId, ct);
    var subs = await db.OnvifEventSubscriptions.Where(x => ids.Contains(x.CameraId))
      .ToDictionaryAsync(x => x.CameraId, ct);
    var filterCounts = await db.OnvifAlarmFilters.Where(f => ids.Contains(f.CameraId))
      .GroupBy(f => f.CameraId)
      .Select(g => new { CameraId = g.Key, Count = g.Count() })
      .ToDictionaryAsync(x => x.CameraId, x => x.Count, ct);
    var armed = await ArmedByCameraAsync(db, ids, ct);

    var result = new List<CameraAnalyticsSources>(setups.Count);
    foreach (var setup in setups)
    {
      var entry = new CameraAnalyticsSources
      {
        CameraId = setup.CameraId,
        OnvifMetadata = new OnvifMetadataSourceState { Accepted = !setup.OnvifMetadataDisabled },
      };

      // Armed is read only to fill the watch's own mirror of it; nothing here is about arming,
      // which is camera-wide and set from the monitor.
      if (watches.TryGetValue(setup.CameraId, out var watch))
        entry.Cv = ToAnalyticsWatchProto(watch, armed.GetValueOrDefault(setup.CameraId));

      // Absent row means the camera is not managed for ONVIF events at all -- which is not the
      // same as being managed and unsubscribed, and the tab shows the two differently.
      subs.TryGetValue(setup.CameraId, out var sub);
      entry.OnvifEvents = new OnvifEventsSourceState
      {
        Managed            = sub is not null,
        Subscribed         = sub?.Subscribed ?? false,
        PollTimeoutSeconds = sub?.PollTimeoutSeconds ?? 0,
        MessageLimit       = sub?.MessageLimit ?? 0,
        FilterCount        = filterCounts.GetValueOrDefault(setup.CameraId),
      };

      result.Add(entry);
    }
    return result;
  }

  private async Task<AnalyticsWatch> ToAnalyticsWatchProtoAsync(DbAnalyticsWatch row, CancellationToken ct)
    => ToAnalyticsWatchProto(row, await GetCameraArmedAsync(row.CameraId, ct));

  private static AnalyticsWatch ToAnalyticsWatchProto(DbAnalyticsWatch row, bool armed)
  {
    var proto = new AnalyticsWatch
    {
      CameraId         = row.CameraId,
      StreamName       = row.StreamName,
      Enabled          = row.Enabled,
      MinConfidence    = row.MinConfidence,
      SampleFps        = (uint)row.SampleFps,
      Armed            = armed,
      AttachDebugCrops = row.AttachDebugCrops,
      TimeoutSeconds   = (uint)row.TimeoutSeconds,
      StoreEmbeddings  = row.StoreEmbeddings,
    };
    proto.Classes.AddRange(row.Classes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    return proto;
  }

  // ── PTZ calibration (1:1 per camera) ──────────────────────────────────────

  public async Task<PtzCalibration?> GetPtzCalibrationAsync(string cameraId, CancellationToken ct)
  {
    await using var db = await dbFactory.CreateDbContextAsync(ct);
    var row = await db.PtzCalibrations.FindAsync([cameraId], ct);
    return row is null ? null : ToPtzCalibrationProto(row);
  }

  public async Task<PtzCalibration> UpsertPtzCalibrationAsync(PtzCalibration calibration, CancellationToken ct)
  {
    await using var db = await dbFactory.CreateDbContextAsync(ct);
    var entity = await db.PtzCalibrations.FindAsync([calibration.CameraId], ct)
                 ?? db.PtzCalibrations.Add(new DbPtzCalibration { CameraId = calibration.CameraId }).Entity;

    entity.PanDx = calibration.PanDx;
    entity.PanDy = calibration.PanDy;
    entity.TiltDx = calibration.TiltDx;
    entity.TiltDy = calibration.TiltDy;
    entity.Confidence = calibration.Confidence;
    entity.CalibratedAt = calibration.CalibratedAt?.ToDateTime() ?? DateTime.UtcNow;
    entity.Method = calibration.Method switch
    {
      PtzCalibrationMethod.PtzCalibrationWizard => "wizard",
      PtzCalibrationMethod.PtzCalibrationAuto => "auto",
      _ => "auto",
    };

    await db.SaveChangesAsync(ct);
    return ToPtzCalibrationProto(entity);
  }

  public async Task<bool> RemovePtzCalibrationAsync(string cameraId, CancellationToken ct)
  {
    await using var db = await dbFactory.CreateDbContextAsync(ct);
    var row = await db.PtzCalibrations.FindAsync([cameraId], ct);
    if (row is null) return false;
    db.PtzCalibrations.Remove(row);
    await db.SaveChangesAsync(ct);
    return true;
  }

  private static PtzCalibration ToPtzCalibrationProto(DbPtzCalibration row)
  {
    var method = row.Method.Equals("wizard", StringComparison.OrdinalIgnoreCase)
      ? PtzCalibrationMethod.PtzCalibrationWizard
      : PtzCalibrationMethod.PtzCalibrationAuto;
    return new PtzCalibration
    {
      CameraId   = row.CameraId,
      PanDx      = row.PanDx,
      PanDy      = row.PanDy,
      TiltDx     = row.TiltDx,
      TiltDy     = row.TiltDy,
      Confidence = row.Confidence,
      CalibratedAt = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTime(
        DateTime.SpecifyKind(row.CalibratedAt, DateTimeKind.Utc)),
      Method = method,
    };
  }

  // ── Self-guidance policy (1:1 per camera, absent = off) ───────────────────

  public async Task<PtzTrackingPolicy?> GetPtzTrackingPolicyAsync(string cameraId, CancellationToken ct)
  {
    await using var db = await dbFactory.CreateDbContextAsync(ct);
    var row = await db.PtzTrackingPolicies.FindAsync([cameraId], ct);
    return row is null ? null : ToPtzTrackingPolicyProto(row);
  }

  public async Task<IReadOnlyList<PtzTrackingPolicy>> ListPtzTrackingPoliciesAsync(CancellationToken ct)
  {
    await using var db = await dbFactory.CreateDbContextAsync(ct);
    var rows = await db.PtzTrackingPolicies.AsNoTracking().ToListAsync(ct);
    return rows.Select(ToPtzTrackingPolicyProto).ToList();
  }

  public async Task<PtzTrackingPolicy> UpsertPtzTrackingPolicyAsync(
    PtzTrackingPolicy policy, CancellationToken ct)
  {
    await using var db = await dbFactory.CreateDbContextAsync(ct);
    var entity = await db.PtzTrackingPolicies.FindAsync([policy.CameraId], ct)
                 ?? db.PtzTrackingPolicies.Add(new DbPtzTrackingPolicy { CameraId = policy.CameraId }).Entity;

    entity.Enabled = policy.Enabled;
    entity.Kinds = Join(policy.Kinds);
    entity.Sources = Join(policy.Sources);
    // Left alone when the field is unset, rather than cleared. The master belongs to the binding
    // -- UpsertPtzSlaveBindingAsync writes it and RemovePtzSlaveBindingAsync clears it -- so a
    // caller editing what a camera follows must not be able to sever it from its master by simply
    // not mentioning one. Sending an empty string is how it is cleared deliberately.
    if (policy.HasMasterCameraId)
      entity.MasterCameraId = policy.MasterCameraId.Length > 0 ? policy.MasterCameraId : null;

    await db.SaveChangesAsync(ct);
    return ToPtzTrackingPolicyProto(entity);
  }

  // Stored as one text column each, like analytics_watch.classes: short lists, read whole, never
  // queried inside. Blanks are dropped rather than stored so an empty list has exactly one
  // representation, which is what makes "empty means any" unambiguous on the way back out.
  private static string Join(IEnumerable<string> values) =>
    string.Join(',', values.Select(v => v.Trim()).Where(v => v.Length > 0));

  private static PtzTrackingPolicy ToPtzTrackingPolicyProto(DbPtzTrackingPolicy row)
  {
    var proto = new PtzTrackingPolicy { CameraId = row.CameraId, Enabled = row.Enabled };
    proto.Kinds.AddRange(
      row.Kinds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    proto.Sources.AddRange(
      row.Sources.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    if (!string.IsNullOrEmpty(row.MasterCameraId))
      proto.MasterCameraId = row.MasterCameraId;
    return proto;
  }

  // ── Master/slave binding (1:1 per slave, with its survey points) ───────────

  public async Task<PtzSlaveBinding?> GetPtzSlaveBindingAsync(string slaveCameraId, CancellationToken ct)
  {
    await using var db = await dbFactory.CreateDbContextAsync(ct);
    var row = await db.PtzSlaveBindings
      .Include(b => b.Points)
      .AsNoTracking()
      .FirstOrDefaultAsync(b => b.SlaveCameraId == slaveCameraId, ct);
    return row is null ? null : ToPtzSlaveBindingProto(row);
  }

  public async Task<IReadOnlyList<PtzSlaveBinding>> ListPtzSlaveBindingsAsync(CancellationToken ct)
  {
    await using var db = await dbFactory.CreateDbContextAsync(ct);
    var rows = await db.PtzSlaveBindings.Include(b => b.Points).AsNoTracking().ToListAsync(ct);
    return rows.Select(ToPtzSlaveBindingProto).ToList();
  }

  /// <summary>Replaces a slave's binding, survey included.</summary>
  /// <remarks>
  /// <para>
  /// The points are deleted and re-inserted rather than matched up one by one. Adding a point
  /// changes what every other point interpolates to, so the survey is only meaningful whole; and
  /// an ordinal is a position in a list the operator reorders, not an identity worth preserving.
  /// </para>
  /// <para>
  /// This also writes the master onto the camera's tracking policy, in the same transaction. The
  /// two are not independent: the policy's master_camera_id is what makes the tracking service run
  /// an aiming session instead of a self-guiding one, and a binding that named a master the policy
  /// did not would leave the camera guiding itself off its own picture -- with a survey sitting
  /// there looking as though it were in use. The operator names the master once, here, and the
  /// policy follows; a policy row is created switched off if there is none, because marking out a
  /// survey is not the same as deciding to start moving.
  /// </para>
  /// </remarks>
  public async Task<PtzSlaveBinding> UpsertPtzSlaveBindingAsync(
    PtzSlaveBinding binding, CancellationToken ct)
  {
    await using var db = await dbFactory.CreateDbContextAsync(ct);
    var entity = await db.PtzSlaveBindings
      .Include(b => b.Points)
      .FirstOrDefaultAsync(b => b.SlaveCameraId == binding.SlaveCameraId, ct);
    if (entity is null)
    {
      entity = new DbPtzSlaveBinding { SlaveCameraId = binding.SlaveCameraId };
      db.PtzSlaveBindings.Add(entity);
    }

    entity.MasterCameraId = binding.MasterCameraId;
    entity.Method = PtzSlaveBindingMethodNames.ToName(binding.Method);
    entity.CalibratedAt = binding.CalibratedAt?.ToDateTime() ?? DateTime.UtcNow;

    db.PtzSlaveBindingPoints.RemoveRange(entity.Points);
    entity.Points = binding.Points.Select((p, i) => new DbPtzSlaveBindingPoint
    {
      SlaveCameraId = binding.SlaveCameraId,
      Ordinal       = i,
      MasterX       = p.MasterX,
      MasterY       = p.MasterY,
      Pan           = p.HasPan ? p.Pan : null,
      Tilt          = p.HasTilt ? p.Tilt : null,
      PresetToken   = string.IsNullOrEmpty(p.PresetToken) ? null : p.PresetToken,
      PresetName    = string.IsNullOrEmpty(p.PresetName) ? null : p.PresetName,
    }).ToList();

    var policy = await db.PtzTrackingPolicies.FindAsync([binding.SlaveCameraId], ct)
                 ?? db.PtzTrackingPolicies.Add(
                      new DbPtzTrackingPolicy { CameraId = binding.SlaveCameraId }).Entity;
    policy.MasterCameraId = binding.MasterCameraId;

    await db.SaveChangesAsync(ct);
    return ToPtzSlaveBindingProto(entity);
  }

  /// <summary>Removes a slave's binding, and with it the policy's claim to a master.</summary>
  /// <remarks>
  /// Leaving master_camera_id behind would leave the tracking service trying to run an aiming
  /// session with nothing to aim by. Self-guidance itself is left alone: whether the camera should
  /// move at all is a separate decision from whose objects it moves for.
  /// </remarks>
  public async Task<bool> RemovePtzSlaveBindingAsync(string slaveCameraId, CancellationToken ct)
  {
    await using var db = await dbFactory.CreateDbContextAsync(ct);
    var row = await db.PtzSlaveBindings.FindAsync([slaveCameraId], ct);
    if (row is null) return false;
    db.PtzSlaveBindings.Remove(row);

    var policy = await db.PtzTrackingPolicies.FindAsync([slaveCameraId], ct);
    if (policy is not null)
      policy.MasterCameraId = null;

    await db.SaveChangesAsync(ct);
    return true;
  }

  private static PtzSlaveBinding ToPtzSlaveBindingProto(DbPtzSlaveBinding row)
  {
    var proto = new PtzSlaveBinding
    {
      SlaveCameraId  = row.SlaveCameraId,
      MasterCameraId = row.MasterCameraId,
      Method = PtzSlaveBindingMethodNames.FromName(row.Method),
      CalibratedAt = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTime(
        DateTime.SpecifyKind(row.CalibratedAt, DateTimeKind.Utc)),
    };
    foreach (var p in row.Points.OrderBy(p => p.Ordinal))
    {
      var point = new PtzSlaveBindingPoint
      {
        MasterX     = p.MasterX,
        MasterY     = p.MasterY,
        PresetToken = p.PresetToken ?? string.Empty,
        PresetName  = p.PresetName ?? string.Empty,
      };
      if (p.Pan.HasValue) point.Pan = p.Pan.Value;
      if (p.Tilt.HasValue) point.Tilt = p.Tilt.Value;
      proto.Points.Add(point);
    }
    return proto;
  }
}
