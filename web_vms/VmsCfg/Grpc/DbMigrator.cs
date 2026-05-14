using Npgsql;

namespace VmsCfg;

public class DbMigrator(NpgsqlDataSource dataSource, IWebHostEnvironment environment)
{
  private readonly NpgsqlDataSource _dataSource = dataSource;
  private readonly IWebHostEnvironment _environment = environment;

  public async Task MigrateAsync(CancellationToken ct = default)
  {
    await using var connection = await _dataSource.OpenConnectionAsync(ct);
    await EnsureMigrationsTableAsync(connection, ct);

    var applied = await LoadAppliedMigrationsAsync(connection, ct);
    foreach (var migrationPath in Directory.EnumerateFiles(GetMigrationsPath(), "*.sql").Order())
    {
      var migrationName = Path.GetFileName(migrationPath);
      if (applied.Contains(migrationName))
      {
        continue;
      }

      await using var tx = await connection.BeginTransactionAsync(ct);
      await using (var migrationCommand = new NpgsqlCommand(
        await File.ReadAllTextAsync(migrationPath, ct),
        connection,
        tx))
      {
        await migrationCommand.ExecuteNonQueryAsync(ct);
      }

      await using (var markCommand = new NpgsqlCommand(
        "INSERT INTO schema_migrations (name) VALUES ($1);",
        connection,
        tx))
      {
        markCommand.Parameters.AddWithValue(migrationName);
        await markCommand.ExecuteNonQueryAsync(ct);
      }

      await tx.CommitAsync(ct);
    }

    await SeedDefaultMediaMtxServerAsync(connection, ct);
  }

  // Inserts a default MediaMTX server from env vars if the table is empty.
  // Reads VMS_WEBRTC_RTSP_BASE_URL and VMS_WEBRTC_CLIENT_URL.
  private static async Task SeedDefaultMediaMtxServerAsync(NpgsqlConnection connection, CancellationToken ct)
  {
    var rtspUrl   = Environment.GetEnvironmentVariable("VMS_WEBRTC_RTSP_BASE_URL");
    var clientUrl = Environment.GetEnvironmentVariable("VMS_WEBRTC_CLIENT_URL");

    if (string.IsNullOrWhiteSpace(rtspUrl) && string.IsNullOrWhiteSpace(clientUrl))
      return;

    await using var checkCmd = new NpgsqlCommand(
      "SELECT COUNT(*) FROM media_mtx_server;", connection);
    var count = (long)(await checkCmd.ExecuteScalarAsync(ct) ?? 0L);
    if (count > 0)
      return;

    await using var insertCmd = new NpgsqlCommand("""
      INSERT INTO media_mtx_server (name, rtsp_url, webrtc_url)
      VALUES ('Default', $1, $2)
      ON CONFLICT DO NOTHING;
      """, connection);
    insertCmd.Parameters.AddWithValue(rtspUrl ?? "rtsp://localhost:8554");
    insertCmd.Parameters.AddWithValue(clientUrl ?? "http://localhost:8889");
    await insertCmd.ExecuteNonQueryAsync(ct);
  }

  private string GetMigrationsPath()
  {
    var contentRootPath = Path.Combine(_environment.ContentRootPath, "Grpc", "migrations");
    if (Directory.Exists(contentRootPath))
    {
      return contentRootPath;
    }

    return Path.Combine(AppContext.BaseDirectory, "Grpc", "migrations");
  }

  private static async Task EnsureMigrationsTableAsync(
    NpgsqlConnection connection,
    CancellationToken ct)
  {
    await using var command = new NpgsqlCommand("""
      CREATE TABLE IF NOT EXISTS schema_migrations (
        name text PRIMARY KEY,
        applied_at timestamptz NOT NULL DEFAULT now()
      );
      """, connection);
    await command.ExecuteNonQueryAsync(ct);
  }

  private static async Task<HashSet<string>> LoadAppliedMigrationsAsync(
    NpgsqlConnection connection,
    CancellationToken ct)
  {
    await using var command = new NpgsqlCommand(
      "SELECT name FROM schema_migrations;",
      connection);

    var result = new HashSet<string>(StringComparer.Ordinal);
    await using var reader = await command.ExecuteReaderAsync(ct);
    while (await reader.ReadAsync(ct))
    {
      result.Add(reader.GetString(0));
    }

    return result;
  }
}
