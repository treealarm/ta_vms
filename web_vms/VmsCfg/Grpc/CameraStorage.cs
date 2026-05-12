using Npgsql;
using NpgsqlTypes;
using Vms.Cfg;

namespace VmsCfg;

public class CameraStorage(NpgsqlDataSource dataSource)
{
  private readonly NpgsqlDataSource _dataSource = dataSource;

  public async Task SetupCameraAsync(CameraSetup camera, CancellationToken ct)
  {
    await using var connection = await _dataSource.OpenConnectionAsync(ct);
    await using var tx = await connection.BeginTransactionAsync(ct);

    await using (var command = new NpgsqlCommand("""
      INSERT INTO camera_setup (camera_id, server_id, cred_user, cred_pass, onvif_ip, onvif_port)
      VALUES ($1, $2, $3, $4, $5, $6)
      ON CONFLICT (camera_id) DO UPDATE SET
        server_id  = EXCLUDED.server_id,
        cred_user  = EXCLUDED.cred_user,
        cred_pass  = EXCLUDED.cred_pass,
        onvif_ip   = EXCLUDED.onvif_ip,
        onvif_port = EXCLUDED.onvif_port;
      """, connection, tx))
    {
      command.Parameters.AddWithValue(camera.CameraId);
      command.Parameters.AddWithValue(camera.ServerId ?? string.Empty);
      command.Parameters.AddWithValue((object?)camera.Cred?.User ?? DBNull.Value);
      command.Parameters.AddWithValue((object?)camera.Cred?.Pass ?? DBNull.Value);
      command.Parameters.AddWithValue((object?)camera.OnvifConn?.Ip ?? DBNull.Value);
      command.Parameters.AddWithValue((object?)camera.OnvifConn?.Port ?? DBNull.Value);
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
      SELECT camera_id, server_id, cred_user, cred_pass, onvif_ip, onvif_port
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
      SELECT camera_id, server_id, cred_user, cred_pass, onvif_ip, onvif_port
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
      SELECT id, name, mode, pre_record_seconds, post_record_seconds, segment_seconds, is_override_preset
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
        is_override_preset  = EXCLUDED.is_override_preset;
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

  public async Task<bool> RemoveRecordingProfileAsync(string id, CancellationToken ct)
  {
    await using var command = _dataSource.CreateCommand(
      "DELETE FROM recording_profile WHERE id = $1::uuid;");
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
             rp.id,  rp.name,  rp.mode,  rp.pre_record_seconds,  rp.post_record_seconds,  rp.segment_seconds,  rp.is_override_preset,
             ov.id,  ov.name,  ov.mode,  ov.pre_record_seconds,  ov.post_record_seconds,  ov.segment_seconds,  ov.is_override_preset
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

      if (!reader.IsDBNull(12))
        stream.OverrideProfile = ReadRecordingProfileFromRow(reader, offset: 12);

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

    return setup;
  }
}
