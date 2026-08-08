using Npgsql;
using NpgsqlTypes;

namespace VmsFs;

/// <summary>One queued or running camera-side import.</summary>
public sealed record EdgeImportJob(
  Guid JobUid,
  string CameraId,
  string StreamName,
  DateTime RangeStart,
  DateTime RangeEnd,
  Guid? OutageUid,
  string State,
  int Attempts,
  string MediaJobId,
  string Message);

/// <summary>
/// The durable side of camera-side imports: what has been asked for, what is running, what is done.
/// </summary>
/// <remarks>
/// Raw SQL rather than EF for the same reason the rest of VmsFs uses it — claiming a job is a single
/// conditional UPDATE that must be atomic against every other runner, which EF cannot express.
/// </remarks>
public sealed class EdgeImportJobStore(NpgsqlDataSource dataSource)
{
  /// <summary>
  /// Queues a piece of a gap, or does nothing if that exact stretch is already queued or running.
  /// </summary>
  public async Task EnqueueAsync(
    string cameraId, string streamName, DateTime rangeStart, DateTime rangeEnd, Guid? outageUid,
    CancellationToken ct)
  {
    await using var cmd = dataSource.CreateCommand("""
      INSERT INTO edge_import_jobs
        (camera_id, stream_name, range_start_ts, range_end_ts, outage_uid)
      VALUES ($1, $2, $3, $4, $5)
      ON CONFLICT (camera_id, stream_name, range_start_ts, range_end_ts) DO UPDATE SET
        -- A finished job for the same stretch means it is being asked for again: the archive still
        -- lacks it, so the previous outcome is history and the row goes back into the queue.
        state       = CASE WHEN edge_import_jobs.state IN ('queued', 'running')
                        THEN edge_import_jobs.state ELSE 'queued' END,
        outage_uid  = COALESCE(EXCLUDED.outage_uid, edge_import_jobs.outage_uid),
        updated_ts  = now();
      """);
    cmd.Parameters.AddWithValue(NpgsqlDbType.Text, cameraId);
    cmd.Parameters.AddWithValue(NpgsqlDbType.Text, streamName ?? string.Empty);
    cmd.Parameters.AddWithValue(NpgsqlDbType.TimestampTz, rangeStart);
    cmd.Parameters.AddWithValue(NpgsqlDbType.TimestampTz, rangeEnd);
    cmd.Parameters.AddWithValue(NpgsqlDbType.Uuid, (object?)outageUid ?? DBNull.Value);
    await cmd.ExecuteNonQueryAsync(ct);
  }

  /// <summary>
  /// Takes the oldest runnable job for one of <paramref name="cameraIds"/> and leases it, or returns
  /// null when there is nothing to do.
  /// </summary>
  /// <remarks>
  /// A job whose lease has expired is runnable again: the process that held it is gone. This is what
  /// makes a restart mid-pull recoverable — the work is a row, not a stack frame.
  /// </remarks>
  public async Task<EdgeImportJob?> ClaimAsync(
    IReadOnlyCollection<string> cameraIds, TimeSpan lease, CancellationToken ct)
  {
    if (cameraIds.Count == 0)
      return null;

    await using var cmd = dataSource.CreateCommand("""
      UPDATE edge_import_jobs SET
        state       = 'running',
        lease_until = now() + $2::interval,
        attempts    = attempts + 1,
        updated_ts  = now()
      WHERE job_uid = (
        SELECT job_uid FROM edge_import_jobs
        WHERE camera_id = ANY($1)
          AND (state = 'queued'
               OR (state = 'running' AND (lease_until IS NULL OR lease_until <= now())))
        ORDER BY range_start_ts
        LIMIT 1
        FOR UPDATE SKIP LOCKED
      )
      RETURNING job_uid, camera_id, stream_name, range_start_ts, range_end_ts, outage_uid,
                state, attempts, media_job_id, message;
      """);
    cmd.Parameters.AddWithValue(NpgsqlDbType.Array | NpgsqlDbType.Text, cameraIds.ToArray());
    cmd.Parameters.AddWithValue(NpgsqlDbType.Interval, lease);

    await using var reader = await cmd.ExecuteReaderAsync(ct);
    if (!await reader.ReadAsync(ct))
      return null;

    return new EdgeImportJob(
      reader.GetGuid(0),
      reader.GetString(1),
      reader.GetString(2),
      reader.GetDateTime(3),
      reader.GetDateTime(4),
      reader.IsDBNull(5) ? null : reader.GetGuid(5),
      reader.GetString(6),
      reader.GetInt32(7),
      reader.GetString(8),
      reader.GetString(9));
  }

  /// <summary>Remembers which media-server job is doing the work, so a resumed runner can find it.</summary>
  public async Task SetMediaJobAsync(Guid jobUid, string mediaJobId, CancellationToken ct)
  {
    await using var cmd = dataSource.CreateCommand(
      "UPDATE edge_import_jobs SET media_job_id = $2, updated_ts = now() WHERE job_uid = $1;");
    cmd.Parameters.AddWithValue(NpgsqlDbType.Uuid, jobUid);
    cmd.Parameters.AddWithValue(NpgsqlDbType.Text, mediaJobId ?? string.Empty);
    await cmd.ExecuteNonQueryAsync(ct);
  }

  /// <summary>Extends the lease of a job that is still being worked on.</summary>
  public async Task RenewAsync(Guid jobUid, TimeSpan lease, CancellationToken ct)
  {
    await using var cmd = dataSource.CreateCommand(
      "UPDATE edge_import_jobs SET lease_until = now() + $2::interval WHERE job_uid = $1;");
    cmd.Parameters.AddWithValue(NpgsqlDbType.Uuid, jobUid);
    cmd.Parameters.AddWithValue(NpgsqlDbType.Interval, lease);
    await cmd.ExecuteNonQueryAsync(ct);
  }

  public async Task FinishAsync(Guid jobUid, string state, string message, CancellationToken ct)
  {
    await using var cmd = dataSource.CreateCommand("""
      UPDATE edge_import_jobs
      SET state = $2, message = $3, lease_until = NULL, updated_ts = now()
      WHERE job_uid = $1;
      """);
    cmd.Parameters.AddWithValue(NpgsqlDbType.Uuid, jobUid);
    cmd.Parameters.AddWithValue(NpgsqlDbType.Text, state);
    cmd.Parameters.AddWithValue(NpgsqlDbType.Text, message ?? string.Empty);
    await cmd.ExecuteNonQueryAsync(ct);
  }

  /// <summary>Drops everything still outstanding for a gap the operator took charge of.</summary>
  public async Task<int> CancelForOutageAsync(Guid outageUid, CancellationToken ct)
  {
    await using var cmd = dataSource.CreateCommand("""
      UPDATE edge_import_jobs
      SET state = 'cancelled', lease_until = NULL, updated_ts = now()
      WHERE outage_uid = $1 AND state IN ('queued', 'running');
      """);
    cmd.Parameters.AddWithValue(NpgsqlDbType.Uuid, outageUid);
    return await cmd.ExecuteNonQueryAsync(ct);
  }

  /// <summary>
  /// Removes finished jobs that have aged out. Queued and running ones are never touched, however
  /// old: a job with no runner is recovered by its expired lease, not by being deleted.
  /// </summary>
  public async Task<int> DeleteFinishedOlderThanAsync(DateTime cutoffUtc, CancellationToken ct)
  {
    await using var cmd = dataSource.CreateCommand("""
      DELETE FROM edge_import_jobs
      WHERE state NOT IN ('queued', 'running') AND updated_ts < $1;
      """);
    cmd.Parameters.AddWithValue(NpgsqlDbType.TimestampTz, cutoffUtc);
    return await cmd.ExecuteNonQueryAsync(ct);
  }
}
