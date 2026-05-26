using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using Vms.Cfg;

namespace VmsCfg;

public class CameraStorage(NpgsqlDataSource dataSource, IDbContextFactory<VmsCfgDbContext> dbFactory)
{
  private readonly NpgsqlDataSource _dataSource = dataSource;
  private static readonly Guid BuiltinContinuousProfileId = new("00000000-0000-0000-0000-000000000001");

  public async Task SetupCameraAsync(CameraSetup camera, CancellationToken ct)
  {
    await using var connection = await _dataSource.OpenConnectionAsync(ct);
    await using var tx = await connection.BeginTransactionAsync(ct);

    Guid? mediaMtxServerId = !string.IsNullOrEmpty(camera.MediaMtxServerId)
      && Guid.TryParse(camera.MediaMtxServerId, out var parsedMmx) ? parsedMmx : null;

    await using (var command = new NpgsqlCommand("""
      INSERT INTO camera_setup (camera_id, server_id, cred_user, cred_pass, onvif_ip, onvif_port, media_mtx_server_id, onvif_events_enabled)
      VALUES ($1, $2, $3, $4, $5, $6, $7, $8)
      ON CONFLICT (camera_id) DO UPDATE SET
        server_id             = EXCLUDED.server_id,
        cred_user             = EXCLUDED.cred_user,
        cred_pass             = EXCLUDED.cred_pass,
        onvif_ip              = EXCLUDED.onvif_ip,
        onvif_port            = EXCLUDED.onvif_port,
        media_mtx_server_id   = EXCLUDED.media_mtx_server_id,
        onvif_events_enabled  = EXCLUDED.onvif_events_enabled;
      """, connection, tx))
    {
      command.Parameters.AddWithValue(camera.CameraId);
      command.Parameters.AddWithValue(camera.ServerId ?? string.Empty);
      command.Parameters.AddWithValue((object?)camera.Cred?.User ?? DBNull.Value);
      command.Parameters.AddWithValue((object?)camera.Cred?.Pass ?? DBNull.Value);
      command.Parameters.AddWithValue((object?)camera.OnvifConn?.Ip ?? DBNull.Value);
      command.Parameters.AddWithValue((object?)camera.OnvifConn?.Port ?? DBNull.Value);
      command.Parameters.Add(new NpgsqlParameter { Value = (object?)mediaMtxServerId ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Uuid });
      command.Parameters.AddWithValue(camera.OnvifEventsEnabled);
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
        INSERT INTO camera_stream (camera_id, stream_name, profile_token, rtsp_url, web_rtc, recording_profile_id)
        VALUES ($1, $2, $3, $4, $5, $6)
        ON CONFLICT (camera_id, stream_name) DO UPDATE SET
          profile_token        = EXCLUDED.profile_token,
          rtsp_url             = EXCLUDED.rtsp_url,
          web_rtc              = EXCLUDED.web_rtc,
          recording_profile_id = EXCLUDED.recording_profile_id;
        """, connection, tx);
      cmd.Parameters.AddWithValue(cameraId);
      cmd.Parameters.AddWithValue(stream.StreamName);
      cmd.Parameters.AddWithValue(stream.ProfileToken ?? string.Empty);
      cmd.Parameters.AddWithValue(stream.RtspUrl ?? string.Empty);
      cmd.Parameters.AddWithValue(stream.WebRtc);
      cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)profileId ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Uuid });
      await cmd.ExecuteNonQueryAsync(ct);
    }
  }

  public async Task RegisterMediaServerAsync(MediaServerNode server, CancellationToken ct)
  {
    await using var command = _dataSource.CreateCommand("""
      INSERT INTO media_server_node (server_id, status, last_seen)
      VALUES ($1, COALESCE(NULLIF($2, ''), 'online'), now())
      ON CONFLICT (server_id) DO UPDATE SET
        status = EXCLUDED.status,
        last_seen = EXCLUDED.last_seen;
      """);
    command.Parameters.AddWithValue(server.ServerId);
    command.Parameters.AddWithValue(server.Status ?? string.Empty);
    await command.ExecuteNonQueryAsync(ct);
  }

  public async Task<List<MediaServerNode>> ListMediaServersAsync(CancellationToken ct)
  {
    await using var command = _dataSource.CreateCommand("""
      SELECT server_id, status FROM media_server_node ORDER BY server_id;
      """);

    var servers = new List<MediaServerNode>();
    await using var reader = await command.ExecuteReaderAsync(ct);
    while (await reader.ReadAsync(ct))
      servers.Add(new MediaServerNode { ServerId = reader.GetString(0), Status = reader.GetString(1) });
    return servers;
  }

  public async Task<List<CameraSetup>> ListCamerasAsync(string serverId, CancellationToken ct)
  {
    await using var connection = await _dataSource.OpenConnectionAsync(ct);

    var cameras = new List<CameraSetup>();

    await using (var cmd = new NpgsqlCommand("""
      SELECT camera_id, server_id, cred_user, cred_pass, onvif_ip, onvif_port, media_mtx_server_id, onvif_events_enabled,
             EXISTS(SELECT 1 FROM onvif_alarm_filter f WHERE f.camera_id = camera_setup.camera_id) AS has_onvif_filters
      FROM camera_setup
      WHERE $1 = '' OR server_id = $1
      ORDER BY camera_id;
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
      SELECT camera_id, server_id, cred_user, cred_pass, onvif_ip, onvif_port, media_mtx_server_id, onvif_events_enabled,
             EXISTS(SELECT 1 FROM onvif_alarm_filter f WHERE f.camera_id = camera_setup.camera_id) AS has_onvif_filters
      FROM camera_setup
      WHERE camera_id = $1;
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
      SELECT cs.camera_id, cs.stream_name, cs.profile_token, cs.rtsp_url, cs.web_rtc,
             rp.id,  rp.name,  rp.mode,  rp.pre_record_seconds,  rp.post_record_seconds,  rp.segment_seconds,  rp.is_override_preset,  rp.is_builtin,
             ov.id,  ov.name,  ov.mode,  ov.pre_record_seconds,  ov.post_record_seconds,  ov.segment_seconds,  ov.is_override_preset,  ov.is_builtin
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
        RtspUrl      = reader.GetString(3),
        WebRtc       = reader.GetBoolean(4),
      };

      if (!reader.IsDBNull(5))
        stream.RecordingProfile = ReadRecordingProfileFromRow(reader, offset: 5);

      if (!reader.IsDBNull(13))
        stream.OverrideProfile = ReadRecordingProfileFromRow(reader, offset: 13);

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
        Ip   = reader.GetString(4),
        Port = reader.IsDBNull(5) ? 0 : reader.GetInt32(5)
      };

    if (!reader.IsDBNull(6))
      setup.MediaMtxServerId = reader.GetGuid(6).ToString();

    setup.OnvifEventsEnabled = reader.GetBoolean(7);
    setup.HasOnvifFilters    = reader.GetBoolean(8);

    return setup;
  }

  // ── MediaMTX server management ───────────────────────────────────────────

  public async Task<List<MediaMtxServer>> ListMediaMtxServersAsync(CancellationToken ct)
  {
    await using var command = _dataSource.CreateCommand("""
      SELECT id, name, rtsp_url, webrtc_url FROM media_mtx_server ORDER BY name;
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
      INSERT INTO media_mtx_server (id, name, rtsp_url, webrtc_url)
      VALUES (COALESCE(NULLIF($1, '')::uuid, gen_random_uuid()), $2, $3, $4)
      ON CONFLICT (id) DO UPDATE SET
        name       = EXCLUDED.name,
        rtsp_url   = EXCLUDED.rtsp_url,
        webrtc_url = EXCLUDED.webrtc_url
      RETURNING id;
      """);
    command.Parameters.AddWithValue(server.Id ?? string.Empty);
    command.Parameters.AddWithValue(server.Name);
    command.Parameters.AddWithValue(server.RtspUrl);
    command.Parameters.AddWithValue(server.WebrtcUrl);
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
      SELECT m.id, m.name, m.rtsp_url, m.webrtc_url
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
      SELECT id, name, rtsp_url, webrtc_url FROM media_mtx_server ORDER BY random() LIMIT 1;
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
      Id        = reader.GetGuid(0).ToString(),
      Name      = reader.GetString(1),
      RtspUrl   = reader.GetString(2),
      WebrtcUrl = reader.GetString(3),
    };

  // ── Monitor management ────────────────────────────────────────────────────

  public async Task<List<MonitorSetup>> ListMonitorsAsync(CancellationToken ct)
  {
    await using var connection = await _dataSource.OpenConnectionAsync(ct);

    var monitors = new Dictionary<Guid, MonitorSetup>();
    var order    = new List<Guid>();

    await using (var cmd = new NpgsqlCommand("""
      SELECT id, name, layout FROM monitor ORDER BY name;
      """, connection))
    {
      await using var reader = await cmd.ExecuteReaderAsync(ct);
      while (await reader.ReadAsync(ct))
      {
        var id = reader.GetGuid(0);
        monitors[id] = new MonitorSetup
        {
          MonitorId = id.ToString(),
          Name      = reader.GetString(1),
          Layout    = reader.GetString(2),
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

  public async Task<MonitorSetup> UpsertMonitorAsync(MonitorSetup request, CancellationToken ct)
  {
    await using var command = _dataSource.CreateCommand("""
      INSERT INTO monitor (id, name, layout)
      VALUES (COALESCE(NULLIF($1, '')::uuid, gen_random_uuid()), $2, $3)
      ON CONFLICT (id) DO UPDATE SET
        name   = EXCLUDED.name,
        layout = EXCLUDED.layout
      RETURNING id;
      """);
    command.Parameters.AddWithValue(request.MonitorId ?? string.Empty);
    command.Parameters.AddWithValue(request.Name);
    command.Parameters.AddWithValue(request.Layout);
    var id = await command.ExecuteScalarAsync(ct);
    request.MonitorId = id?.ToString() ?? request.MonitorId;
    return request;
  }

  public async Task<bool> RemoveMonitorAsync(string monitorId, CancellationToken ct)
  {
    await using var command = _dataSource.CreateCommand(
      "DELETE FROM monitor WHERE id = $1::uuid;");
    command.Parameters.AddWithValue(monitorId);
    return await command.ExecuteNonQueryAsync(ct) > 0;
  }

  public async Task SetMonitorCamerasAsync(string monitorId, IEnumerable<string> cameraIds, CancellationToken ct)
  {
    await using var connection = await _dataSource.OpenConnectionAsync(ct);
    await using var tx = await connection.BeginTransactionAsync(ct);

    await using (var del = new NpgsqlCommand(
      "DELETE FROM monitor_camera WHERE monitor_id = $1::uuid;", connection, tx))
    {
      del.Parameters.AddWithValue(monitorId);
      await del.ExecuteNonQueryAsync(ct);
    }

    int pos = 0;
    foreach (var cameraId in cameraIds)
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
  }

  public async Task SetMonitorSlotAsync(string monitorId, int slotIndex, string cameraId, CancellationToken ct)
  {
    await using var command = _dataSource.CreateCommand("""
      INSERT INTO monitor_slot (monitor_id, slot_index, camera_id)
      VALUES ($1::uuid, $2, $3)
      ON CONFLICT (monitor_id, slot_index) DO UPDATE SET camera_id = EXCLUDED.camera_id;
      """);
    command.Parameters.AddWithValue(monitorId);
    command.Parameters.AddWithValue(slotIndex);
    command.Parameters.AddWithValue(cameraId);
    await command.ExecuteNonQueryAsync(ct);
  }

  // ── ONVIF alarm filters ───────────────────────────────────────────────────

  public async Task<List<OnvifAlarmFilter>> ListOnvifAlarmFiltersAsync(string cameraId, CancellationToken ct)
  {
    await using var db = await dbFactory.CreateDbContextAsync(ct);
    var rows = await db.OnvifAlarmFilters
      .Where(f => f.CameraId == cameraId)
      .OrderBy(f => f.CreatedAt)
      .ToListAsync(ct);
    return rows.Select(ToProto).ToList();
  }

  public async Task<OnvifAlarmFilter> UpsertOnvifAlarmFilterAsync(OnvifAlarmFilter filter, CancellationToken ct)
  {
    await using var command = _dataSource.CreateCommand("""
      INSERT INTO onvif_alarm_filter (id, camera_id, filter_text, timeout_seconds)
      VALUES (COALESCE(NULLIF($1, '')::uuid, gen_random_uuid()), $2, $3, $4)
      ON CONFLICT (id) DO UPDATE SET
        filter_text     = EXCLUDED.filter_text,
        timeout_seconds = EXCLUDED.timeout_seconds
      RETURNING id, camera_id, filter_text, timeout_seconds;
      """);
    command.Parameters.AddWithValue(filter.Id ?? string.Empty);
    command.Parameters.AddWithValue(filter.CameraId);
    command.Parameters.AddWithValue(filter.FilterText);
    command.Parameters.AddWithValue(filter.TimeoutSeconds);

    await using var reader = await command.ExecuteReaderAsync(ct);
    await reader.ReadAsync(ct);
    return new OnvifAlarmFilter
    {
      Id             = reader.GetGuid(0).ToString(),
      CameraId       = reader.GetString(1),
      FilterText     = reader.GetString(2),
      TimeoutSeconds = reader.GetInt32(3),
    };
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
    };
}
