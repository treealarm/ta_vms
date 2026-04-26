using Google.Protobuf.WellKnownTypes;
using Npgsql;
using NpgsqlTypes;
using Vms.Fs;

namespace VmsFs;

public class ClipStorage(NpgsqlDataSource dataSource)
{
  private readonly NpgsqlDataSource _dataSource = dataSource;

  public async Task<string> IngestAsync(ClipMeta clip, CancellationToken ct)
  {
    var clipUid = string.IsNullOrWhiteSpace(clip.ClipUid)
      ? Guid.NewGuid()
      : Guid.Parse(clip.ClipUid);

    await using var command = _dataSource.CreateCommand("""
      INSERT INTO clips (clip_uid, file_path, camera_id, start_ts, end_ts, size_bytes)
      VALUES ($1, $2, $3, $4, $5, $6)
      ON CONFLICT (file_path) DO UPDATE SET
        camera_id = EXCLUDED.camera_id,
        start_ts = EXCLUDED.start_ts,
        end_ts = EXCLUDED.end_ts,
        size_bytes = EXCLUDED.size_bytes
      RETURNING clip_uid;
      """);

    command.Parameters.AddWithValue(NpgsqlDbType.Uuid, clipUid);
    command.Parameters.AddWithValue(NpgsqlDbType.Text, clip.FilePath);
    command.Parameters.AddWithValue(NpgsqlDbType.Text, clip.CameraId);
    command.Parameters.AddWithValue(NpgsqlDbType.TimestampTz, clip.StartTs.ToDateTime());
    command.Parameters.AddWithValue(
      NpgsqlDbType.TimestampTz,
      clip.EndTs is null ? DBNull.Value : clip.EndTs.ToDateTime());
    command.Parameters.AddWithValue(NpgsqlDbType.Bigint, (long)clip.SizeBytes);

    var storedClipUid = (Guid)(await command.ExecuteScalarAsync(ct) ?? clipUid);
    return storedClipUid.ToString();
  }

  public async Task<List<Recording>> GetRecordingsAsync(
    GetRecordingsRequest request,
    CancellationToken ct)
  {
    await using var command = _dataSource.CreateCommand("""
      SELECT camera_id, file_path, start_ts, end_ts, size_bytes
      FROM clips
      WHERE camera_id = $1
        AND ($2::timestamptz IS NULL OR COALESCE(end_ts, start_ts) >= $2)
        AND ($3::timestamptz IS NULL OR start_ts <= $3)
      ORDER BY start_ts DESC
      LIMIT $4;
      """);

    command.Parameters.AddWithValue(NpgsqlDbType.Text, request.CameraId);
    command.Parameters.AddWithValue(
      NpgsqlDbType.TimestampTz,
      request.FromTs is not null ? request.FromTs.ToDateTime() : DBNull.Value);
    command.Parameters.AddWithValue(
      NpgsqlDbType.TimestampTz,
      request.ToTs is not null ? request.ToTs.ToDateTime() : DBNull.Value);
    command.Parameters.AddWithValue(NpgsqlDbType.Integer, (int)(request.Limit == 0 ? 100 : request.Limit));

    var recordings = new List<Recording>();
    await using var reader = await command.ExecuteReaderAsync(ct);
    while (await reader.ReadAsync(ct))
    {
      recordings.Add(new Recording
      {
        CameraId = reader.GetString(0),
        FilePath = reader.GetString(1),
        StartTs = Timestamp.FromDateTime(reader.GetDateTime(2).ToUniversalTime()),
        EndTs = reader.IsDBNull(3) ? null : Timestamp.FromDateTime(reader.GetDateTime(3).ToUniversalTime()),
        SizeBytes = (ulong)reader.GetInt64(4)
      });
    }

    return recordings;
  }
}
