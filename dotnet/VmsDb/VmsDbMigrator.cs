using Npgsql;

namespace VmsDb;

// Applies a service's SQL migrations to its own database: one schema_migrations table per
// database, one session-level advisory lock per service, and a hook for whatever a service must
// seed after the schema exists. What differs between services — the name in messages, the lock key
// and the seeds — is a constructor argument; the machinery is shared.
//
// The migrations themselves stay where they always were: <contentRoot>/Grpc/migrations/*.sql,
// copied beside the binary by each service's csproj, applied in filename order, each in its own
// transaction and recorded by filename.
public sealed class VmsDbMigrator
{
  // The one file that creates every table of a service from nothing. A database carrying an older,
  // longer migration history cannot be upgraded to it: the baseline's CREATE TABLE statements meet
  // tables that already exist.
  private const string BaselineMigration = "0000_init.sql";

  private readonly NpgsqlDataSource _dataSource;
  private readonly string _serviceName;
  private readonly string _contentRootPath;
  private readonly string _advisoryLockExpression;
  private readonly Func<NpgsqlConnection, CancellationToken, Task>? _afterMigrations;

  // advisoryLockExpression is a SQL expression, not a value: VmsCfg keeps the
  // hashtext('ta_vms_schema_migrations') it has always used, the other services their numeric
  // keys. Callers pass a constant written in their own source, never anything built from input.
  public VmsDbMigrator(
    NpgsqlDataSource dataSource,
    string serviceName,
    string contentRootPath,
    string advisoryLockExpression,
    Func<NpgsqlConnection, CancellationToken, Task>? afterMigrations = null)
  {
    _dataSource = dataSource;
    _serviceName = serviceName;
    _contentRootPath = contentRootPath;
    _advisoryLockExpression = advisoryLockExpression;
    _afterMigrations = afterMigrations;
  }

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
        throw new InvalidOperationException(MissingDatabaseMessage(), ex);
      }
      catch (NpgsqlException ex)
      {
        Console.Error.WriteLine(
          $"{_serviceName}: Postgres not ready, retrying in 5s: {ex.Message}");
        await Task.Delay(5_000, ct);
      }
    }
    await using var _ = connection;

    // One replica migrates, the rest wait and then find nothing left to do. Reading
    // schema_migrations and applying a file are not atomic together, so two instances starting on
    // the same database would both see the file as missing; the second would then fail on the
    // primary key of schema_migrations, or — worse, for a service with seeds — insert a second
    // "default" row. A session-level advisory lock is the cheapest way to serialise a step that
    // only runs at startup; it is released explicitly at the end, and by the connection closing if
    // we throw.
    await using (var lockCmd = new NpgsqlCommand(
      $"SELECT pg_advisory_lock({_advisoryLockExpression});", connection))
    {
      await lockCmd.ExecuteNonQueryAsync(ct);
    }

    try
    {
      await EnsureMigrationsTableAsync(connection, ct);

      var applied = await LoadAppliedMigrationsAsync(connection, ct);
      await RejectPreBaselineSchemaAsync(connection, applied, ct);

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

      if (_afterMigrations != null)
      {
        await _afterMigrations(connection, ct);
      }
    }
    finally
    {
      // CancellationToken.None: a cancelled migration must still release the lock, or the next
      // replica of this service would wait on it for the life of the connection that took it.
      //
      // What is released here is a session-level lock, so a failure to release it costs nothing
      // but the wait until this connection closes, and that is what the disposal below does. It
      // must not become the exception the caller sees: a connection that the failure itself broke
      // answers "Connection is not open" here, and that would replace the one error worth reading
      // with one that says only that something else went wrong first.
      if (connection.State == System.Data.ConnectionState.Open)
      {
        try
        {
          await using var unlockCmd = new NpgsqlCommand(
            $"SELECT pg_advisory_unlock({_advisoryLockExpression});", connection);
          await unlockCmd.ExecuteNonQueryAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
          Console.Error.WriteLine(
            $"{_serviceName}: could not release the migration lock: {ex.Message}");
        }
      }
    }
  }

  // A database whose schema predates the baseline holds tables the baseline would recreate. Left
  // alone, the first CREATE TABLE fails with "already exists" and every service dies on startup
  // with a message that says nothing about what to do; instead the reason and the remedy are named
  // here, before anything is applied. `schema_migrations` is the migrator's own table and is the
  // only one that is expected on a database with nothing applied.
  private async Task RejectPreBaselineSchemaAsync(
    NpgsqlConnection connection,
    HashSet<string> applied,
    CancellationToken ct)
  {
    if (!Directory.EnumerateFiles(GetMigrationsPath(), "*.sql")
      .Any(path => Path.GetFileName(path) == BaselineMigration))
    {
      return;
    }

    if (applied.Contains(BaselineMigration))
    {
      return;
    }

    await using var command = new NpgsqlCommand("""
      SELECT EXISTS (
        SELECT 1 FROM information_schema.tables
        WHERE table_schema = 'public'
          AND table_name <> 'schema_migrations'
      );
      """, connection);

    if ((bool)(await command.ExecuteScalarAsync(ct))!)
    {
      var database = new NpgsqlConnectionStringBuilder(_dataSource.ConnectionString).Database;
      throw new InvalidOperationException(
        $"{_serviceName}: database '{database}' has tables but no '{BaselineMigration}' baseline — " +
        "its schema predates the baseline, and the baseline cannot be applied over it. " +
        "Recreate the database (drop it and run the preparation step again); no data is carried " +
        "over from a pre-baseline schema.");
    }
  }

  private string MissingDatabaseMessage()
  {
    var database = new NpgsqlConnectionStringBuilder(_dataSource.ConnectionString).Database;
    return $"{_serviceName}: database '{database}' does not exist on the configured Postgres server. " +
      "Create it with the preparation step — `docker compose run --rm db-init`, or " +
      "scripts/prepare-databases.sh against the target server.";
  }

  private string GetMigrationsPath()
  {
    var contentRootPath = Path.Combine(_contentRootPath, "Grpc", "migrations");
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
