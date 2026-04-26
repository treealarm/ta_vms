using Npgsql;
using Vms.Cfg;

namespace VmsCfg
{
  public class CameraStorage(NpgsqlDataSource dataSource)
  {
    private readonly NpgsqlDataSource _dataSource = dataSource;

    public async Task SetupCameraAsync(CameraSetup camera, CancellationToken ct)
    {
      await using var connection = await _dataSource.OpenConnectionAsync(ct);
      await using var tx = await connection.BeginTransactionAsync(ct);

      await using (var command = new NpgsqlCommand("""
        INSERT INTO camera_setup (camera_id, server_id, rtsp_url, cred_user, cred_pass, onvif_ip, onvif_port)
        VALUES ($1, $2, $3, $4, $5, $6, $7)
        ON CONFLICT (camera_id) DO UPDATE SET
          server_id = EXCLUDED.server_id,
          rtsp_url = EXCLUDED.rtsp_url,
          cred_user = EXCLUDED.cred_user,
          cred_pass = EXCLUDED.cred_pass,
          onvif_ip = EXCLUDED.onvif_ip,
          onvif_port = EXCLUDED.onvif_port;
        """, connection, tx))
      {
        command.Parameters.AddWithValue(camera.CameraId);
        command.Parameters.AddWithValue(camera.ServerId ?? string.Empty);
        command.Parameters.AddWithValue(camera.RtspUrl ?? string.Empty);
        command.Parameters.AddWithValue((object?)camera.Cred?.User ?? DBNull.Value);
        command.Parameters.AddWithValue((object?)camera.Cred?.Pass ?? DBNull.Value);
        command.Parameters.AddWithValue((object?)camera.OnvifConn?.Ip ?? DBNull.Value);
        command.Parameters.AddWithValue((object?)camera.OnvifConn?.Port ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(ct);
      }

      if (camera.State is not null)
      {
        await UpsertStateAsync(connection, tx, camera.State, ct);
      }
      else
      {
        await using var command = new NpgsqlCommand("""
          INSERT INTO camera_state (camera_id, recording, web_rtc)
          VALUES ($1, false, false)
          ON CONFLICT (camera_id) DO NOTHING;
          """, connection, tx);
        command.Parameters.AddWithValue(camera.CameraId);
        await command.ExecuteNonQueryAsync(ct);
      }

      await tx.CommitAsync(ct);
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
        SELECT s.camera_id, s.server_id, s.rtsp_url, s.cred_user, s.cred_pass, s.onvif_ip, s.onvif_port,
               COALESCE(st.recording, false), COALESCE(st.web_rtc, false)
        FROM camera_setup s
        LEFT JOIN camera_state st ON st.camera_id = s.camera_id
        WHERE $1 = '' OR s.server_id = $1
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
        SELECT s.camera_id, s.server_id, s.rtsp_url, s.cred_user, s.cred_pass, s.onvif_ip, s.onvif_port,
               COALESCE(st.recording, false), COALESCE(st.web_rtc, false)
        FROM camera_setup s
        LEFT JOIN camera_state st ON st.camera_id = s.camera_id
        WHERE s.camera_id = $1;
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
      await using var command = new NpgsqlCommand("""
        INSERT INTO camera_state (camera_id, recording, web_rtc)
        VALUES ($1, $2, $3)
        ON CONFLICT (camera_id) DO UPDATE SET
          recording = EXCLUDED.recording,
          web_rtc = EXCLUDED.web_rtc;
        """, connection, tx);
      command.Parameters.AddWithValue(state.CameraId);
      command.Parameters.AddWithValue(state.Recording);
      command.Parameters.AddWithValue(state.WebRtc);
      await command.ExecuteNonQueryAsync(ct);
    }

    private static CameraSetup ReadCameraSetup(NpgsqlDataReader reader)
    {
      var setup = new CameraSetup
      {
        CameraId = reader.GetString(0),
        ServerId = reader.GetString(1),
        RtspUrl = reader.GetString(2),
        State = new CameraState
        {
          CameraId = reader.GetString(0),
          Recording = reader.GetBoolean(7),
          WebRtc = reader.GetBoolean(8)
        }
      };

      if (!reader.IsDBNull(3) || !reader.IsDBNull(4))
      {
        setup.Cred = new Credentials
        {
          User = reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
          Pass = reader.IsDBNull(4) ? string.Empty : reader.GetString(4)
        };
      }

      if (!reader.IsDBNull(5))
      {
        setup.OnvifConn = new OnvifConnection
        {
          Ip = reader.GetString(5),
          Port = reader.IsDBNull(6) ? 0 : reader.GetInt32(6)
        };
      }

      return setup;
    }
  }
}
