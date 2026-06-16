using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace VmsCfg;

public class DbMigrator(
  NpgsqlDataSource dataSource,
  IWebHostEnvironment environment,
  IDbContextFactory<VmsCfgDbContext> dbContextFactory)
{
  private readonly NpgsqlDataSource _dataSource = dataSource;
  private readonly IWebHostEnvironment _environment = environment;
  private readonly IDbContextFactory<VmsCfgDbContext> _dbContextFactory = dbContextFactory;

  public async Task MigrateAsync(CancellationToken ct = default)
  {
    NpgsqlConnection connection;
    while (true)
    {
      try
      {
        connection = await _dataSource.OpenConnectionAsync(ct);
        break;
      }
      catch (NpgsqlException ex)
      {
        Console.Error.WriteLine($"VmsCfg: Postgres not ready, retrying in 5s: {ex.Message}");
        await Task.Delay(5_000, ct);
      }
    }
    await using var _ = connection;
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
    await SeedDefaultStorageAsync(ct);
  }

  // Inserts a default Media Router from env var VMS_MTX_HOST if the table is empty.
  // Optional — if VMS_MTX_HOST is not set, no seed is created.
  private static async Task SeedDefaultMediaMtxServerAsync(NpgsqlConnection connection, CancellationToken ct)
  {
    var host = Environment.GetEnvironmentVariable("VMS_MTX_HOST");
    if (string.IsNullOrWhiteSpace(host))
      return;

    await using var checkCmd = new NpgsqlCommand(
      "SELECT COUNT(*) FROM media_mtx_server;", connection);
    var count = (long)(await checkCmd.ExecuteScalarAsync(ct) ?? 0L);
    if (count > 0)
      return;

    await using var insertCmd = new NpgsqlCommand("""
      INSERT INTO media_mtx_server (name, host, webrtc_host, rtsp_port, webrtc_port, api_port)
      VALUES ('Default', $1, '', 8554, 8889, 9997)
      ON CONFLICT DO NOTHING;
      """, connection);
    insertCmd.Parameters.AddWithValue(host);
    await insertCmd.ExecuteNonQueryAsync(ct);
  }

  // Inserts a default storage pointing at VMS_STORAGE_BASE_PATH if the table is empty,
  // so a fresh deployment has a usable storage instead of an empty list.
  private async Task SeedDefaultStorageAsync(CancellationToken ct)
  {
    var rootPath = Environment.GetEnvironmentVariable("VMS_STORAGE_BASE_PATH");
    if (string.IsNullOrWhiteSpace(rootPath))
      return;

    await using var db = await _dbContextFactory.CreateDbContextAsync(ct);
    if (await db.Storages.AnyAsync(ct))
      return;

    db.Storages.Add(new DbStorage { StorageId = "default", RootPath = rootPath, ReservedBytes = 0 });
    await db.SaveChangesAsync(ct);
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
