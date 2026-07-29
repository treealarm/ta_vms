using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using Vms.Cfg;

namespace VmsCfg;

public class CameraStorage(NpgsqlDataSource dataSource, IDbContextFactory<VmsCfgDbContext> dbFactory)
{
  private readonly NpgsqlDataSource _dataSource = dataSource;
  private static readonly Guid BuiltinContinuousProfileId = new("00000000-0000-0000-0000-000000000001");

  private readonly IReadOnlyList<(string ServerId, string FsId)> _serverPairs = ParseServerPairs();

  private static IReadOnlyList<(string ServerId, string FsId)> ParseServerPairs()
  {
    var raw = Environment.GetEnvironmentVariable("MEDIA_SERVER_PAIRS")
      ?? throw new InvalidOperationException("Required environment variable 'MEDIA_SERVER_PAIRS' is not set");

    var pairs = new List<(string, string)>();
    foreach (var entry in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    {
      var sep = entry.IndexOf(':');
      if (sep < 1)
        throw new InvalidOperationException($"Invalid MEDIA_SERVER_PAIRS entry '{entry}': expected 'server_id:fs_id'");
      pairs.Add((entry[..sep], entry[(sep + 1)..]));
    }

    if (pairs.Count == 0)
      throw new InvalidOperationException("MEDIA_SERVER_PAIRS must contain at least one 'server_id:fs_id' pair");

    return pairs;
  }

  public IReadOnlyList<(string ServerId, string FsId)> GetServerPairs() => _serverPairs;

  public bool IsValidServerId(string serverId) =>
    _serverPairs.Any(p => p.ServerId == serverId);

  public async Task SetupCameraAsync(CameraSetup camera, CancellationToken ct)
  {
    await using var connection = await _dataSource.OpenConnectionAsync(ct);
    await using var tx = await connection.BeginTransactionAsync(ct);

    Guid? mediaMtxServerId = !string.IsNullOrEmpty(camera.MediaMtxServerId)
      && Guid.TryParse(camera.MediaMtxServerId, out var parsedMmx) ? parsedMmx : null;

    await using (var command = new NpgsqlCommand("""
      INSERT INTO camera_setup (camera_id, server_id, cred_user, cred_pass, onvif_ip, onvif_port, onvif_xaddr, media_mtx_server_id, disabled, name)
      VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10)
      ON CONFLICT (camera_id) DO UPDATE SET
        server_id           = EXCLUDED.server_id,
        cred_user           = EXCLUDED.cred_user,
        cred_pass           = EXCLUDED.cred_pass,
        onvif_ip            = EXCLUDED.onvif_ip,
        onvif_port          = EXCLUDED.onvif_port,
        onvif_xaddr         = EXCLUDED.onvif_xaddr,
        media_mtx_server_id = EXCLUDED.media_mtx_server_id,
        disabled            = EXCLUDED.disabled,
        name                = EXCLUDED.name;
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
        INSERT INTO camera_stream (camera_id, stream_name, profile_token, path, web_rtc, recording_profile_id, disabled)
        VALUES ($1, $2, $3, $4, $5, $6, $7)
        ON CONFLICT (camera_id, stream_name) DO UPDATE SET
          profile_token        = EXCLUDED.profile_token,
          path                 = EXCLUDED.path,
          web_rtc              = EXCLUDED.web_rtc,
          recording_profile_id = EXCLUDED.recording_profile_id,
          disabled             = EXCLUDED.disabled;
        """, connection, tx);
      cmd.Parameters.AddWithValue(cameraId);
      cmd.Parameters.AddWithValue(stream.StreamName);
      cmd.Parameters.AddWithValue(stream.ProfileToken ?? string.Empty);
      cmd.Parameters.AddWithValue(stream.Path ?? string.Empty);
      cmd.Parameters.AddWithValue(stream.WebRtc);
      cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)profileId ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Uuid });
      cmd.Parameters.Add(new NpgsqlParameter { Value = (stream.HasDisabled && stream.Disabled) ? (object)true : DBNull.Value, NpgsqlDbType = NpgsqlDbType.Boolean });
      await cmd.ExecuteNonQueryAsync(ct);
    }
  }

  public async Task<List<CameraSetup>> ListCamerasAsync(string serverId, CancellationToken ct)
  {
    await using var connection = await _dataSource.OpenConnectionAsync(ct);

    var cameras = new List<CameraSetup>();

    await using (var cmd = new NpgsqlCommand("""
      SELECT camera_setup.camera_id, server_id, cred_user, cred_pass, onvif_ip, onvif_port, onvif_xaddr, media_mtx_server_id,
             EXISTS(SELECT 1 FROM onvif_event_subscription s WHERE s.camera_id = camera_setup.camera_id AND s.subscribed = true) AS onvif_events_enabled,
             EXISTS(SELECT 1 FROM onvif_alarm_filter f WHERE f.camera_id = camera_setup.camera_id) AS has_onvif_filters,
             EXISTS(SELECT 1 FROM onvif_event_subscription s WHERE s.camera_id = camera_setup.camera_id) AS onvif_managed,
             disabled,
             COALESCE(oes.poll_timeout_seconds, 5) AS poll_timeout_seconds,
             COALESCE(oes.message_limit, 1024) AS message_limit,
             camera_setup.name
      FROM camera_setup
      LEFT JOIN onvif_event_subscription oes ON oes.camera_id = camera_setup.camera_id
      WHERE $1 = '' OR server_id = $1
      ORDER BY camera_setup.name, camera_setup.camera_id;
      """, connection))
    {
      cmd.Parameters.AddWithValue(serverId ?? string.Empty);
      await using var reader = await cmd.ExecuteReaderAsync(ct);
      while (await reader.ReadAsync(ct))
        cameras.Add(ReadCameraSetup(reader));
    }

    await LoadStreamsAsync(connection, cameras, ct);
    return cameras;
  }

  public async Task<(bool Found, CameraSetup? Setup)> GetCameraAsync(string cameraId, CancellationToken ct)
  {
    await using var connection = await _dataSource.OpenConnectionAsync(ct);

    CameraSetup? setup = null;

    await using (var cmd = new NpgsqlCommand("""
      SELECT camera_setup.camera_id, server_id, cred_user, cred_pass, onvif_ip, onvif_port, onvif_xaddr, media_mtx_server_id,
             EXISTS(SELECT 1 FROM onvif_event_subscription s WHERE s.camera_id = camera_setup.camera_id AND s.subscribed = true) AS onvif_events_enabled,
             EXISTS(SELECT 1 FROM onvif_alarm_filter f WHERE f.camera_id = camera_setup.camera_id) AS has_onvif_filters,
             EXISTS(SELECT 1 FROM onvif_event_subscription s WHERE s.camera_id = camera_setup.camera_id) AS onvif_managed,
             disabled,
             COALESCE(oes.poll_timeout_seconds, 5) AS poll_timeout_seconds,
             COALESCE(oes.message_limit, 1024) AS message_limit,
             camera_setup.name
      FROM camera_setup
      LEFT JOIN onvif_event_subscription oes ON oes.camera_id = camera_setup.camera_id
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
    return (true, setup);
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
      UPDATE camera_stream
      SET override_profile_id = $3::uuid
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
      UPDATE camera_stream
      SET override_profile_id = NULL
      WHERE camera_id = $1 AND stream_name = $2;
      """);
    command.Parameters.AddWithValue(cameraId);
    command.Parameters.AddWithValue(streamName);
    await command.ExecuteNonQueryAsync(ct);
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

  public async Task<bool> ActivateAlarmOverrideAsync(string cameraId, string? streamName, DateTime until, CancellationToken ct)
  {
    await using var db = await dbFactory.CreateDbContextAsync(ct);
    var rows = await db.CameraStreams
      .Where(cs => cs.CameraId == cameraId
                && (streamName == null || cs.StreamName == streamName)
                && (cs.AlarmOverrideUntil != null
                    || (cs.OverrideProfileId == null
                        && (cs.RecordingProfileId == null
                            || cs.RecordingProfile!.Mode == "off"))))
      .ExecuteUpdateAsync(s => s
        .SetProperty(cs => cs.OverrideProfileId, BuiltinContinuousProfileId)
        .SetProperty(cs => cs.AlarmOverrideUntil,
          cs => cs.AlarmOverrideUntil == null || cs.AlarmOverrideUntil < until ? until : cs.AlarmOverrideUntil),
        ct);
    return rows > 0;
  }

  public async Task<bool> ClearAlarmOverrideAsync(string cameraId, string streamName, CancellationToken ct)
  {
    await using var db = await dbFactory.CreateDbContextAsync(ct);
    var rows = await db.CameraStreams
      .Where(cs => cs.CameraId == cameraId && cs.StreamName == streamName && cs.AlarmOverrideUntil != null)
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
             cs.disabled
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

    return setup;
  }

  // ── MediaMTX server management ───────────────────────────────────────────

  public async Task<List<MediaMtxServer>> ListMediaMtxServersAsync(CancellationToken ct)
  {
    await using var command = _dataSource.CreateCommand("""
      SELECT id, name, host, webrtc_host, rtsp_port, webrtc_port, api_port FROM media_mtx_server ORDER BY name;
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
      INSERT INTO media_mtx_server (id, name, host, webrtc_host, rtsp_port, webrtc_port, api_port)
      VALUES (COALESCE(NULLIF($1, '')::uuid, gen_random_uuid()), $2, $3, $4, $5, $6, $7)
      ON CONFLICT (id) DO UPDATE SET
        name        = EXCLUDED.name,
        host        = EXCLUDED.host,
        webrtc_host = EXCLUDED.webrtc_host,
        rtsp_port   = EXCLUDED.rtsp_port,
        webrtc_port = EXCLUDED.webrtc_port,
        api_port    = EXCLUDED.api_port
      RETURNING id;
      """);
    command.Parameters.AddWithValue(server.Id ?? string.Empty);
    command.Parameters.AddWithValue(server.Name);
    command.Parameters.AddWithValue(server.Host);
    command.Parameters.AddWithValue(server.WebrtcHost);
    command.Parameters.AddWithValue(server.RtspPort);
    command.Parameters.AddWithValue(server.WebrtcPort);
    command.Parameters.AddWithValue(server.ApiPort);
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

  public async Task<MediaMtxServer?> GetOrAssignMediaMtxServerAsync(string cameraId, CancellationToken ct)
  {
    await using var connection = await _dataSource.OpenConnectionAsync(ct);

    // Check if camera already has a server assigned and it still exists
    await using (var cmd = new NpgsqlCommand("""
      SELECT m.id, m.name, m.host, m.webrtc_host, m.rtsp_port, m.webrtc_port, m.api_port
      FROM camera_setup cs
      JOIN media_mtx_server m ON m.id = cs.media_mtx_server_id
      WHERE cs.camera_id = $1;
      """, connection))
    {
      cmd.Parameters.AddWithValue(cameraId);
      await using var reader = await cmd.ExecuteReaderAsync(ct);
      if (await reader.ReadAsync(ct))
        return ReadMediaMtxServer(reader);
    }

    // Pick a random available server and assign it
    await using var tx = await connection.BeginTransactionAsync(ct);
    MediaMtxServer? picked = null;

    await using (var pickCmd = new NpgsqlCommand("""
      SELECT id, name, host, webrtc_host, rtsp_port, webrtc_port, api_port FROM media_mtx_server ORDER BY random() LIMIT 1;
      """, connection, tx))
    {
      await using var reader = await pickCmd.ExecuteReaderAsync(ct);
      if (await reader.ReadAsync(ct))
        picked = ReadMediaMtxServer(reader);
    }

    if (picked is null)
      return null;

    await using (var assignCmd = new NpgsqlCommand("""
      UPDATE camera_setup SET media_mtx_server_id = $2::uuid WHERE camera_id = $1;
      """, connection, tx))
    {
      assignCmd.Parameters.AddWithValue(cameraId);
      assignCmd.Parameters.AddWithValue(picked.Id);
      await assignCmd.ExecuteNonQueryAsync(ct);
    }

    await tx.CommitAsync(ct);
    return picked;
  }

  private static MediaMtxServer ReadMediaMtxServer(NpgsqlDataReader reader) =>
    new()
    {
      Id         = reader.GetGuid(0).ToString(),
      Name       = reader.GetString(1),
      Host       = reader.GetString(2),
      WebrtcHost = reader.GetString(3),
      RtspPort   = reader.GetInt32(4),
      WebrtcPort = reader.GetInt32(5),
      ApiPort    = reader.GetInt32(6),
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

    await using (var cmd = new NpgsqlCommand("""
      SELECT monitor_id, slot_index, camera_id FROM monitor_slot
      WHERE monitor_id = ANY($1);
      """, connection))
    {
      cmd.Parameters.Add(new NpgsqlParameter { Value = idArray, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Uuid });
      await using var reader = await cmd.ExecuteReaderAsync(ct);
      while (await reader.ReadAsync(ct))
      {
        if (monitors.TryGetValue(reader.GetGuid(0), out var m))
          m.Slots.Add(new MonitorSlot { SlotIndex = reader.GetInt32(1), CameraId = reader.GetString(2) });
      }
    }

    return order.Select(id => monitors[id]).ToList();
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

  public async Task SetOnvifArmedAsync(string cameraId, bool armed, CancellationToken ct)
  {
    await using var cmd = _dataSource.CreateCommand(
      "UPDATE onvif_event_subscription SET subscribed = $2 WHERE camera_id = $1;");
    cmd.Parameters.AddWithValue(cameraId);
    cmd.Parameters.AddWithValue(armed);
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
    var result = new List<AnalyticsWatch>();
    foreach (var row in rows)
      result.Add(await ToAnalyticsWatchProtoAsync(row, ct));
    return result;
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

  private async Task<AnalyticsWatch> ToAnalyticsWatchProtoAsync(DbAnalyticsWatch row, CancellationToken ct)
  {
    var proto = new AnalyticsWatch
    {
      CameraId         = row.CameraId,
      StreamName       = row.StreamName,
      Enabled          = row.Enabled,
      MinConfidence    = row.MinConfidence,
      SampleFps        = (uint)row.SampleFps,
      Armed            = await GetCameraArmedAsync(row.CameraId, ct),
      AttachDebugCrops = row.AttachDebugCrops,
      TimeoutSeconds   = (uint)row.TimeoutSeconds,
    };
    proto.Classes.AddRange(row.Classes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    return proto;
  }
}
