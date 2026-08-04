using Npgsql;
using NpgsqlTypes;

namespace VmsFs;

/// <summary>One row of <c>source_outages</c>, as the rest of VmsFs needs it.</summary>
public sealed record SourceOutage(
  Guid OutageUid,
  string CameraId,
  string StreamName,
  DateTime StartedTs,
  DateTime? EndedTs,
  string Cause,
  bool RecordingExpected,
  string State,
  long FilledMs,
  int Attempts,
  string Message);

/// <summary>
/// Reads and writes the outage intervals derived from source heartbeats.
/// </summary>
/// <remarks>
/// Raw SQL rather than EF here for the same reason <see cref="ClipStorage"/> uses it: every write is
/// an upsert keyed on <c>(camera_id, stream_name, started_ts)</c>, which EF cannot express.
/// </remarks>
public sealed class SourceOutageStore(NpgsqlDataSource dataSource)
{
  /// <summary>
  /// Opens an outage, or extends the previous one when the source was only briefly alive between
  /// them. Returns the row's start, which the caller uses as the interval's identity.
  /// </summary>
  /// <remarks>
  /// The coalescing is not cosmetic: a camera flapping every 20 s past a 15 s threshold would
  /// otherwise mint some 4000 rows a day on its own. A blink shorter than the grace window is not a
  /// recovery, and treating it as one makes the table unbounded and the operator's view unreadable.
  /// </remarks>
  public async Task<DateTime> OpenAsync(
    string cameraId,
    string streamName,
    DateTime startedTs,
    string cause,
    bool recordingExpected,
    TimeSpan coalesceWindow,
    CancellationToken ct)
  {
    await using var cmd = dataSource.CreateCommand("""
      WITH recent AS (
        SELECT outage_uid, started_ts
        FROM source_outages
        WHERE camera_id = $1 AND stream_name = $2
          AND ended_ts IS NOT NULL
          AND ended_ts >= $3 - $6::interval
        ORDER BY started_ts DESC
        LIMIT 1
      ), reopened AS (
        UPDATE source_outages SET ended_ts = NULL
        WHERE outage_uid = (SELECT outage_uid FROM recent)
        RETURNING started_ts
      ), inserted AS (
        INSERT INTO source_outages (camera_id, stream_name, started_ts, cause, recording_expected)
        SELECT $1, $2, $3, $4, $5
        WHERE NOT EXISTS (SELECT 1 FROM recent)
        ON CONFLICT (camera_id, stream_name, started_ts) DO NOTHING
        RETURNING started_ts
      )
      -- Explicit priority rather than relying on UNION ALL evaluation order: reopened wins over
      -- inserted, and $3 is only the answer when neither branch produced a row.
      SELECT started_ts FROM (
        SELECT 0 AS pri, started_ts FROM reopened
        UNION ALL SELECT 1, started_ts FROM inserted
        UNION ALL SELECT 2, $3
      ) x ORDER BY pri LIMIT 1;
      """);
    cmd.Parameters.AddWithValue(NpgsqlDbType.Text, cameraId);
    cmd.Parameters.AddWithValue(NpgsqlDbType.Text, streamName);
    cmd.Parameters.AddWithValue(NpgsqlDbType.TimestampTz, startedTs);
    cmd.Parameters.AddWithValue(NpgsqlDbType.Text, cause);
    cmd.Parameters.AddWithValue(NpgsqlDbType.Boolean, recordingExpected);
    cmd.Parameters.AddWithValue(NpgsqlDbType.Interval, coalesceWindow);

    var result = await cmd.ExecuteScalarAsync(ct);
    return result is DateTime dt ? dt : startedTs;
  }

  /// <summary>Closes the open interval of a stream. No-op when nothing is open.</summary>
  public async Task CloseAsync(string cameraId, string streamName, DateTime endedTs, CancellationToken ct)
  {
    await using var cmd = dataSource.CreateCommand("""
      UPDATE source_outages SET ended_ts = $3
      WHERE camera_id = $1 AND stream_name = $2 AND ended_ts IS NULL;
      """);
    cmd.Parameters.AddWithValue(NpgsqlDbType.Text, cameraId);
    cmd.Parameters.AddWithValue(NpgsqlDbType.Text, streamName);
    cmd.Parameters.AddWithValue(NpgsqlDbType.TimestampTz, endedTs);
    await cmd.ExecuteNonQueryAsync(ct);
  }

  /// <summary>Streams that are silent right now — the tracker's state after a restart.</summary>
  public async Task<List<(string CameraId, string StreamName)>> ListOpenAsync(CancellationToken ct)
  {
    await using var cmd = dataSource.CreateCommand(
      "SELECT camera_id, stream_name FROM source_outages WHERE ended_ts IS NULL;");
    var result = new List<(string, string)>();
    await using var reader = await cmd.ExecuteReaderAsync(ct);
    while (await reader.ReadAsync(ct))
      result.Add((reader.GetString(0), reader.GetString(1)));
    return result;
  }

  /// <summary>
  /// Last clip end for a stream — the only honest interval start after a node-wide restart, when
  /// no in-memory last-frame timestamp survived. The archive stopped exactly there.
  /// </summary>
  public async Task<DateTime?> LastClipEndAsync(string cameraId, string streamName, CancellationToken ct)
  {
    await using var cmd = dataSource.CreateCommand("""
      SELECT MAX(COALESCE(end_ts, start_ts)) FROM clips
      WHERE camera_id = $1 AND stream_name = $2;
      """);
    cmd.Parameters.AddWithValue(NpgsqlDbType.Text, cameraId);
    cmd.Parameters.AddWithValue(NpgsqlDbType.Text, streamName);
    var value = await cmd.ExecuteScalarAsync(ct);
    return value is DateTime dt ? dt : null;
  }

  public async Task<List<SourceOutage>> ListAsync(
    string? cameraId, DateTime? fromTs, DateTime? toTs, int limit, CancellationToken ct)
  {
    await using var cmd = dataSource.CreateCommand("""
      SELECT outage_uid, camera_id, stream_name, started_ts, ended_ts, cause,
             recording_expected, state, filled_ms, attempts, message
      FROM source_outages
      WHERE ($1::text IS NULL OR camera_id = $1)
        AND ($2::timestamptz IS NULL OR COALESCE(ended_ts, now()) >= $2)
        AND ($3::timestamptz IS NULL OR started_ts <= $3)
      ORDER BY started_ts DESC
      LIMIT $4;
      """);
    cmd.Parameters.AddWithValue(NpgsqlDbType.Text, (object?)cameraId ?? DBNull.Value);
    cmd.Parameters.AddWithValue(NpgsqlDbType.TimestampTz, (object?)fromTs ?? DBNull.Value);
    cmd.Parameters.AddWithValue(NpgsqlDbType.TimestampTz, (object?)toTs ?? DBNull.Value);
    cmd.Parameters.AddWithValue(NpgsqlDbType.Integer, limit <= 0 ? 200 : limit);

    var result = new List<SourceOutage>();
    await using var reader = await cmd.ExecuteReaderAsync(ct);
    while (await reader.ReadAsync(ct))
    {
      result.Add(new SourceOutage(
        reader.GetGuid(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetDateTime(3),
        reader.IsDBNull(4) ? null : reader.GetDateTime(4),
        reader.GetString(5),
        reader.GetBoolean(6),
        reader.GetString(7),
        reader.GetInt64(8),
        reader.GetInt32(9),
        reader.GetString(10)));
    }
    return result;
  }

  /// <summary>
  /// Removes closed intervals older than the retention window. Rows still open are never removed
  /// regardless of age: forgetting an outage that is happening right now would make the system
  /// report everything as healthy. State plays no part — an unpatched gap this old is unpatchable
  /// anyway, since the camera holds about an hour.
  /// </summary>
  public async Task<int> DeleteClosedOlderThanAsync(DateTime cutoffUtc, CancellationToken ct)
  {
    await using var cmd = dataSource.CreateCommand(
      "DELETE FROM source_outages WHERE ended_ts IS NOT NULL AND ended_ts < $1;");
    cmd.Parameters.AddWithValue(NpgsqlDbType.TimestampTz, cutoffUtc);
    return await cmd.ExecuteNonQueryAsync(ct);
  }
}
