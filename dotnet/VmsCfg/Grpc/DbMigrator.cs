using System.Net;
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
      catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.InvalidCatalogName)
      {
        // The server answered, it just has no such database — retrying cannot fix that. Without
        // this the failure is indistinguishable from an unreachable server and shows up as
        // "Postgres not ready" forever, which sends everyone looking in the wrong place.
        throw new InvalidOperationException(MissingDatabaseMessage(_dataSource, "VmsCfg"), ex);
      }
      catch (NpgsqlException ex)
      {
        Console.Error.WriteLine($"VmsCfg: Postgres not ready, retrying in 5s: {ex.Message}");
        await Task.Delay(5_000, ct);
      }
    }
    await using var _ = connection;

    // One replica migrates, the rest wait and then find nothing left to do. Reading
    // schema_migrations and applying a file are not atomic together, so two instances starting on
    // the same database would both see the file as missing; the second would then fail on the
    // primary key of schema_migrations, or — worse, for the seeds below — insert a second "default"
    // row. A session-level advisory lock is the cheapest way to serialise a step that only runs at
    // startup; it is released explicitly at the end, and by the connection closing if we throw.
    await using (var lockCmd = new NpgsqlCommand(
      "SELECT pg_advisory_lock(hashtext('ta_vms_schema_migrations'));", connection))
    {
      await lockCmd.ExecuteNonQueryAsync(ct);
    }

    try
    {
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
    await NodeRegistry.SeedConfiguredAsync(connection, ct);
    await SeedDefaultStorageAsync(ct);
    }
    finally
    {
      await using var unlockCmd = new NpgsqlCommand(
        "SELECT pg_advisory_unlock(hashtext('ta_vms_schema_migrations'));", connection);
      await unlockCmd.ExecuteNonQueryAsync(CancellationToken.None);
    }
  }

  // Inserts a default Media Router from VMS_MTX_HOST when the table is empty, then performs the
  // one-time credential backfill used when upgrading from a schema that had no per-router auth.
  // MediaMTX credential env vars are seed-only: runtime consumers read this row through VmsCfg.
  private static async Task SeedDefaultMediaMtxServerAsync(NpgsqlConnection connection, CancellationToken ct)
  {
    var host = Environment.GetEnvironmentVariable("VMS_MTX_HOST");
    var publishUser = (Environment.GetEnvironmentVariable("VMS_MEDIAMTX_PUBLISH_USER") ?? string.Empty).Trim();
    var publishPass = Environment.GetEnvironmentVariable("VMS_MEDIAMTX_PUBLISH_PASS") ?? string.Empty;
    var readUser = (Environment.GetEnvironmentVariable("VMS_MEDIAMTX_READ_USER") ?? string.Empty).Trim();
    var readPass = Environment.GetEnvironmentVariable("VMS_MEDIAMTX_READ_PASS") ?? string.Empty;

    if (!string.IsNullOrWhiteSpace(host))
    {
      await using var insertCmd = new NpgsqlCommand("""
        INSERT INTO media_mtx_server (
          name, host, webrtc_host, rtsp_port, webrtc_port, api_port, stun_url,
          publish_user, publish_pass, read_user, read_pass)
        SELECT 'Default', $1, '', 8554, 8889, 9997, 'stun:stun.l.google.com:19302',
               $2, $3, $4, $5
        WHERE NOT EXISTS (SELECT 1 FROM media_mtx_server);
        """, connection);
      insertCmd.Parameters.AddWithValue(host);
      insertCmd.Parameters.AddWithValue(publishUser);
      insertCmd.Parameters.AddWithValue(publishPass);
      insertCmd.Parameters.AddWithValue(readUser);
      insertCmd.Parameters.AddWithValue(readPass);
      await insertCmd.ExecuteNonQueryAsync(ct);
    }

    // A standalone installation that became a deployment's core seeded its row with the loopback,
    // which no other machine can reach; VMS_MTX_HOST names the core's address now. Only an IP
    // address counts: a host name (compose's "mediamtx") says nothing about other machines.
    if (IPAddress.TryParse(host, out var address) && !IPAddress.IsLoopback(address))
    {
      await using var rehostCmd = new NpgsqlCommand("""
        UPDATE media_mtx_server SET host = $1
        WHERE host IN ('127.0.0.1', 'localhost', '::1');
        """, connection);
      rehostCmd.Parameters.AddWithValue(host!);
      await rehostCmd.ExecuteNonQueryAsync(ct);
    }

    if (publishUser.Length == 0 && readUser.Length == 0)
      return;

    await using var backfillCmd = new NpgsqlCommand("""
      UPDATE media_mtx_server
      SET publish_user = $1, publish_pass = $2, read_user = $3, read_pass = $4
      WHERE name = 'Default' AND publish_user = '' AND read_user = '';
      """, connection);
    backfillCmd.Parameters.AddWithValue(publishUser);
    backfillCmd.Parameters.AddWithValue(publishPass);
    backfillCmd.Parameters.AddWithValue(readUser);
    backfillCmd.Parameters.AddWithValue(readPass);
    await backfillCmd.ExecuteNonQueryAsync(ct);
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

    // Same reservation a storage created by hand starts with: recording must not be allowed to
    // consume the last byte of the volume, which on a single-box install also holds the database
    // and the logs.
    const long defaultReservedBytes = 5L * 1024 * 1024 * 1024;
    db.Storages.Add(new DbStorage
    {
      StorageId = "default",
      RootPath = rootPath,
      ReservedBytes = defaultReservedBytes,
    });
    await db.SaveChangesAsync(ct);
  }

  // Kept local to each service's DbMigrator, the same way BuildPostgresConnectionString is —
  // these three migrators are deliberately independent copies, not a shared base class.
  private static string MissingDatabaseMessage(NpgsqlDataSource dataSource, string service)
  {
    var database = new NpgsqlConnectionStringBuilder(dataSource.ConnectionString).Database;
    return $"{service}: database '{database}' does not exist on the configured Postgres server. " +
      "Create it with the preparation step — `docker compose run --rm db-init`, or " +
      "scripts/prepare-databases.sh against the target server.";
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
