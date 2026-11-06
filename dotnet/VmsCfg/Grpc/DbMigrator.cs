using System.Net;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using VmsDb;

namespace VmsCfg;

// The VmsCfg half of the migration step: the advisory lock key it has always used and the seeds
// that must exist before the first request. The migration machinery itself is VmsDb's.
public class DbMigrator(
  NpgsqlDataSource dataSource,
  IWebHostEnvironment environment,
  IDbContextFactory<VmsCfgDbContext> dbContextFactory)
{
  private readonly NpgsqlDataSource _dataSource = dataSource;
  private readonly IWebHostEnvironment _environment = environment;
  private readonly IDbContextFactory<VmsCfgDbContext> _dbContextFactory = dbContextFactory;

  public Task MigrateAsync(CancellationToken ct = default) =>
    new VmsDbMigrator(
      _dataSource,
      "VmsCfg",
      _environment.ContentRootPath,
      "hashtext('ta_vms_schema_migrations')",
      SeedAsync).MigrateAsync(ct);

  private async Task SeedAsync(NpgsqlConnection connection, CancellationToken ct)
  {
    await SeedDefaultMediaMtxServerAsync(connection, ct);
    await NodeRegistry.SeedConfiguredAsync(connection, ct);
    await SeedDefaultStorageAsync(ct);
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
    // Storage ids are UUIDs; this well-known one names the volume the deployment started with, so
    // a reinstall that recreates the database and seeds again lands on the same id.
    db.Storages.Add(new DbStorage
    {
      StorageId = "00000000-0000-0000-0000-0000000000d1",
      RootPath = rootPath,
      ReservedBytes = defaultReservedBytes,
    });
    await db.SaveChangesAsync(ct);
  }
}
