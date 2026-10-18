using Npgsql;

namespace VmsFs;

public class DbMigrator(NpgsqlDataSource dataSource, IWebHostEnvironment environment)
{
  private readonly NpgsqlDataSource _dataSource = dataSource;
  private readonly IWebHostEnvironment _environment = environment;

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
        throw new InvalidOperationException(MissingDatabaseMessage(_dataSource, "VmsFs"), ex);
      }
      catch (NpgsqlException ex)
      {
        Console.Error.WriteLine($"VmsFs: Postgres not ready, retrying in 5s: {ex.Message}");
        await Task.Delay(5_000, ct);
      }
    }
    await using var _ = connection;

    // Serialize concurrent migrations across multiple instances sharing the same DB.
    await using (var lockCmd = new NpgsqlCommand("SELECT pg_advisory_lock(7743512879);", connection))
      await lockCmd.ExecuteNonQueryAsync(ct);

    try
    {
      await EnsureMigrationsTableAsync(connection, ct);

      var applied = await LoadAppliedMigrationsAsync(connection, ct);
      foreach (var migrationPath in Directory.EnumerateFiles(GetMigrationsPath(), "*.sql").Order())
      {
        var migrationName = Path.GetFileName(migrationPath);
        if (applied.Contains(migrationName))
          continue;

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
    finally
    {
      await using var unlockCmd = new NpgsqlCommand("SELECT pg_advisory_unlock(7743512879);", connection);
      await unlockCmd.ExecuteNonQueryAsync(ct);
    }
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
