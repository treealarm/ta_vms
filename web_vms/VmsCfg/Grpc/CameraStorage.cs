using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;
using Vms.Cfg;

namespace VmsCfg;

public class CameraStorage(NpgsqlDataSource dataSource)
{
  private readonly NpgsqlDataSource _dataSource = dataSource;

  private record StreamRow(
    [property: JsonPropertyName("n")] string Name,
    [property: JsonPropertyName("p")] string Profile,
    [property: JsonPropertyName("u")] string Url);

  public async Task SetupCameraAsync(CameraSetup camera, CancellationToken ct)
  {
    var preRec = camera.PreAlarm != null ? camera.PreAlarm.PreRecordSeconds : 30u;
    var postRec = camera.PreAlarm != null ? camera.PreAlarm.PostRecordSeconds : 30u;
    var seg = camera.PreAlarm != null ? Math.Max(1u, camera.PreAlarm.SegmentSeconds) : 5u;

    await using var connection = await _dataSource.OpenConnectionAsync(ct);
    await using var tx = await connection.BeginTransactionAsync(ct);

    await using (var command = new NpgsqlCommand("""
      INSERT INTO camera_setup (
        camera_id, server_id, cred_user, cred_pass, onvif_ip, onvif_port,
        pre_record_seconds, post_record_seconds, prealarm_segment_seconds)
      VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9)
      ON CONFLICT (camera_id) DO UPDATE SET
        server_id = EXCLUDED.server_id,
        cred_user = EXCLUDED.cred_user,
        cred_pass = EXCLUDED.cred_pass,
        onvif_ip = EXCLUDED.onvif_ip,
        onvif_port = EXCLUDED.onvif_port,
        pre_record_seconds = EXCLUDED.pre_record_seconds,
        post_record_seconds = EXCLUDED.post_record_seconds,
        prealarm_segment_seconds = EXCLUDED.prealarm_segment_seconds;
      """, connection, tx))
    {
      command.Parameters.AddWithValue(camera.CameraId);
      command.Parameters.AddWithValue(camera.ServerId ?? string.Empty);
      command.Parameters.AddWithValue((object?)camera.Cred?.User ?? DBNull.Value);
      command.Parameters.AddWithValue((object?)camera.Cred?.Pass ?? DBNull.Value);
      command.Parameters.AddWithValue((object?)camera.OnvifConn?.Ip ?? DBNull.Value);
      command.Parameters.AddWithValue((object?)camera.OnvifConn?.Port ?? DBNull.Value);
      command.Parameters.AddWithValue((int)preRec);
      command.Parameters.AddWithValue((int)postRec);
      command.Parameters.AddWithValue((int)seg);
      await command.ExecuteNonQueryAsync(ct);
    }

    if (camera.State is not null)
    {
      await UpsertStateAsync(connection, tx, camera.State, ct);
    }
    else
    {
      await using var command = new NpgsqlCommand("""
        INSERT INTO camera_state (camera_id, recording, web_rtc, recording_mode)
        VALUES ($1, false, false, 'off')
        ON CONFLICT (camera_id) DO NOTHING;
        """, connection, tx);
      command.Parameters.AddWithValue(camera.CameraId);
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
      await using var cmd = new NpgsqlCommand("""
        INSERT INTO camera_stream (camera_id, stream_name, profile_token, rtsp_url)
        VALUES ($1, $2, $3, $4)
        ON CONFLICT (camera_id, stream_name) DO UPDATE SET
          profile_token = EXCLUDED.profile_token,
          rtsp_url = EXCLUDED.rtsp_url;
        """, connection, tx);
      cmd.Parameters.AddWithValue(cameraId);
      cmd.Parameters.AddWithValue(stream.StreamName);
      cmd.Parameters.AddWithValue(stream.ProfileToken ?? string.Empty);
      cmd.Parameters.AddWithValue(stream.RtspUrl ?? string.Empty);
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
      SELECT server_id, status
      FROM media_server_node
      ORDER BY server_id;
      """);

    var servers = new List<MediaServerNode>();
    await using var reader = await command.ExecuteReaderAsync(ct);
    while (await reader.ReadAsync(ct))
    {
      servers.Add(new MediaServerNode
      {
        ServerId = reader.GetString(0),
        Status = reader.GetString(1)
      });
    }

    return servers;
  }

  public async Task<bool> SetCameraStateAsync(CameraState state, CancellationToken ct)
  {
    await using var connection = await _dataSource.OpenConnectionAsync(ct);
    await using var tx = await connection.BeginTransactionAsync(ct);

    await using (var existsCommand = new NpgsqlCommand(
      "SELECT EXISTS (SELECT 1 FROM camera_setup WHERE camera_id = $1);",
      connection,
      tx))
    {
      existsCommand.Parameters.AddWithValue(state.CameraId);
      var exists = (bool)(await existsCommand.ExecuteScalarAsync(ct) ?? false);
      if (!exists)
      {
        return false;
      }
    }

    await UpsertStateAsync(connection, tx, state, ct);
    await tx.CommitAsync(ct);
    return true;
  }

  public async Task<List<CameraSetup>> ListCamerasAsync(string serverId, CancellationToken ct)
  {
    await using var command = _dataSource.CreateCommand("""
      SELECT s.camera_id, s.server_id, s.cred_user, s.cred_pass, s.onvif_ip, s.onvif_port,
             COALESCE(st.recording, false), COALESCE(st.web_rtc, false),
             COALESCE(NULLIF(TRIM(st.recording_mode), ''), 'off'),
             s.pre_record_seconds, s.post_record_seconds, s.prealarm_segment_seconds,
             COALESCE(
               json_agg(json_build_object('n', cs.stream_name, 'p', cs.profile_token, 'u', cs.rtsp_url)
                        ORDER BY cs.stream_name)
               FILTER (WHERE cs.stream_name IS NOT NULL),
               '[]'
             ) AS streams
      FROM camera_setup s
      LEFT JOIN camera_state st ON st.camera_id = s.camera_id
      LEFT JOIN camera_stream cs ON cs.camera_id = s.camera_id
      WHERE $1 = '' OR s.server_id = $1
      GROUP BY s.camera_id, s.server_id, s.cred_user, s.cred_pass, s.onvif_ip, s.onvif_port,
               st.recording, st.web_rtc, st.recording_mode,
               s.pre_record_seconds, s.post_record_seconds, s.prealarm_segment_seconds
      ORDER BY s.camera_id;
      """);
    command.Parameters.AddWithValue(serverId ?? string.Empty);

    var cameras = new List<CameraSetup>();
    await using var reader = await command.ExecuteReaderAsync(ct);
    while (await reader.ReadAsync(ct))
    {
      cameras.Add(ReadCameraSetup(reader));
    }

    return cameras;
  }

  public async Task<(bool Found, CameraSetup? Setup)> GetCameraAsync(string cameraId, CancellationToken ct)
  {
    await using var command = _dataSource.CreateCommand("""
      SELECT s.camera_id, s.server_id, s.cred_user, s.cred_pass, s.onvif_ip, s.onvif_port,
             COALESCE(st.recording, false), COALESCE(st.web_rtc, false),
             COALESCE(NULLIF(TRIM(st.recording_mode), ''), 'off'),
             s.pre_record_seconds, s.post_record_seconds, s.prealarm_segment_seconds,
             COALESCE(
               json_agg(json_build_object('n', cs.stream_name, 'p', cs.profile_token, 'u', cs.rtsp_url)
                        ORDER BY cs.stream_name)
               FILTER (WHERE cs.stream_name IS NOT NULL),
               '[]'
             ) AS streams
      FROM camera_setup s
      LEFT JOIN camera_state st ON st.camera_id = s.camera_id
      LEFT JOIN camera_stream cs ON cs.camera_id = s.camera_id
      WHERE s.camera_id = $1
      GROUP BY s.camera_id, s.server_id, s.cred_user, s.cred_pass, s.onvif_ip, s.onvif_port,
               st.recording, st.web_rtc, st.recording_mode,
               s.pre_record_seconds, s.post_record_seconds, s.prealarm_segment_seconds;
      """);
    command.Parameters.AddWithValue(cameraId);

    await using var reader = await command.ExecuteReaderAsync(ct);
    if (!await reader.ReadAsync(ct))
    {
      return (false, null);
    }

    return (true, ReadCameraSetup(reader));
  }

  public async Task<bool> RemoveCameraAsync(string cameraId, CancellationToken ct)
  {
    await using var command = _dataSource.CreateCommand(
      "DELETE FROM camera_setup WHERE camera_id = $1;");
    command.Parameters.AddWithValue(cameraId);
    return await command.ExecuteNonQueryAsync(ct) > 0;
  }

  private static async Task UpsertStateAsync(
    NpgsqlConnection connection,
    NpgsqlTransaction tx,
    CameraState state,
    CancellationToken ct)
  {
    var modeStr = RecordingModeToDb(state.RecordingMode);
    var recordingEnabled = state.RecordingMode != RecordingMode.RecordingOff;

    await using var command = new NpgsqlCommand("""
      INSERT INTO camera_state (camera_id, recording, web_rtc, recording_mode)
      VALUES ($1, $2, $3, $4)
      ON CONFLICT (camera_id) DO UPDATE SET
        recording = EXCLUDED.recording,
        web_rtc = EXCLUDED.web_rtc,
        recording_mode = EXCLUDED.recording_mode;
      """, connection, tx);
    command.Parameters.AddWithValue(state.CameraId);
    command.Parameters.AddWithValue(recordingEnabled);
    command.Parameters.AddWithValue(state.WebRtc);
    command.Parameters.AddWithValue(modeStr);
    await command.ExecuteNonQueryAsync(ct);
  }

  private static string RecordingModeToDb(RecordingMode mode) => mode switch
  {
    RecordingMode.RecordingContinuous => "continuous",
    RecordingMode.RecordingPrealarm => "prealarm",
    _ => "off",
  };

  private static RecordingMode ParseRecordingModeFromDb(string raw)
  {
    return raw.Trim().ToLowerInvariant() switch
    {
      "continuous" => RecordingMode.RecordingContinuous,
      "prealarm" => RecordingMode.RecordingPrealarm,
      _ => RecordingMode.RecordingOff,
    };
  }

  private static CameraSetup ReadCameraSetup(NpgsqlDataReader reader)
  {
    // col 0: camera_id, 1: server_id, 2: cred_user, 3: cred_pass,
    // col 4: onvif_ip, 5: onvif_port, 6: recording, 7: web_rtc,
    // col 8: recording_mode, 9: pre_record_seconds, 10: post_record_seconds,
    // col 11: prealarm_segment_seconds, 12: streams (json)
    var webRtc = reader.GetBoolean(7);
    var recordingMode = reader.GetString(8);
    var preRec = reader.GetInt32(9);
    var postRec = reader.GetInt32(10);
    var seg = reader.GetInt32(11);
    var streamsJson = reader.GetString(12);

    var setup = new CameraSetup
    {
      CameraId = reader.GetString(0),
      ServerId = reader.GetString(1),
      PreAlarm = new PreAlarmRecordingSettings
      {
        PreRecordSeconds = (uint)Math.Clamp(preRec, 0, int.MaxValue),
        PostRecordSeconds = (uint)Math.Clamp(postRec, 0, int.MaxValue),
        SegmentSeconds = (uint)Math.Clamp(seg, 1, int.MaxValue)
      },
      State = new CameraState
      {
        CameraId = reader.GetString(0),
        WebRtc = webRtc,
        RecordingMode = ParseRecordingModeFromDb(recordingMode)
      }
    };

    if (!reader.IsDBNull(2) || !reader.IsDBNull(3))
    {
      setup.Cred = new Credentials
      {
        User = reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
        Pass = reader.IsDBNull(3) ? string.Empty : reader.GetString(3)
      };
    }

    if (!reader.IsDBNull(4))
    {
      setup.OnvifConn = new OnvifConnection
      {
        Ip = reader.GetString(4),
        Port = reader.IsDBNull(5) ? 0 : reader.GetInt32(5)
      };
    }

    var rows = JsonSerializer.Deserialize<List<StreamRow>>(streamsJson) ?? [];
    foreach (var row in rows)
    {
      setup.Streams.Add(new CameraStream
      {
        StreamName = row.Name,
        ProfileToken = row.Profile,
        RtspUrl = row.Url
      });
    }

    return setup;
  }
}
