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
