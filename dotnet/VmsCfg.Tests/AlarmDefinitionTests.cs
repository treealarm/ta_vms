using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Npgsql;
using VmsDb;

namespace VmsCfg.Tests;

// The definitions are seeds, so only a migrated database can say whether they are there and carry
// the policies the alarm lifecycle depends on. Runs when VMS_CFG_TEST_DSN points at a scratch
// database, like the device-settings tests.
public class AlarmDefinitionPostgresTests
{
  private static readonly string? Dsn = Environment.GetEnvironmentVariable("VMS_CFG_TEST_DSN");

  [Fact]
  public async Task AFreshDatabaseCarriesTheThreeDefinitions()
  {
    if (string.IsNullOrWhiteSpace(Dsn))
      return;

    await using (var migrationSource = new NpgsqlDataSourceBuilder(Dsn).Build())
    {
      await new VmsDbMigrator(
        migrationSource, "VmsCfgTest", AppContext.BaseDirectory, "hashtext('ta_vms_schema_migrations')")
        .MigrateAsync();
    }

    await using var source = new NpgsqlDataSourceBuilder(Dsn).Build();
    var options = new DbContextOptionsBuilder<VmsCfgDbContext>()
      .UseNpgsql(source)
      .Options;
    var storage = new AlarmDefinitionStorage(new PooledDbContextFactory<VmsCfgDbContext>(options));

    var definitions = await storage.ListAsync(default);

    Assert.Equal(["retention", "sensor", "subscription"], definitions.Select(d => d.Name));
    Assert.Equal("auto_on_normal", definitions.Single(d => d.Name == "retention").AckPolicy);
    Assert.Equal("required", definitions.Single(d => d.Name == "sensor").AckPolicy);
    Assert.Equal("required", definitions.Single(d => d.Name == "subscription").AckPolicy);
    Assert.All(definitions, d => Assert.Equal("warning", d.Severity));

    // The ids travel as UUIDs in every alarm row VmsMap writes, so they must be parseable and
    // stable across a re-run of the migration.
    Assert.All(definitions, d => Assert.True(Guid.TryParse(d.Id, out _)));
    Assert.Equal(
      definitions.Select(d => d.Id).OrderBy(id => id),
      (await storage.ListAsync(default)).Select(d => d.Id).OrderBy(id => id));
  }
}
