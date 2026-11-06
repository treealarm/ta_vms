using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Npgsql;
using Vms.Cfg;
using VmsDb;

namespace VmsCfg.Tests;

// Saving a subscription is where a bad selector becomes a message rather than a rule that silently
// never fires, and where the version a run snapshots is assigned. Both need a migrated database:
// the table and the camera lookup are the database's. Runs when VMS_CFG_TEST_DSN points at a
// scratch database, like the device-settings and alarm-definition tests.
public class EventSubscriptionStoragePostgresTests
{
  private static readonly string? Dsn = Environment.GetEnvironmentVariable("VMS_CFG_TEST_DSN");

  [Fact]
  public async Task AnUpsertChecksTheCameraAndGrowsTheVersion()
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
    var storage = new EventSubscriptionStorage(new PooledDbContextFactory<VmsCfgDbContext>(options));

    var cameraId = Guid.NewGuid().ToString();
    var subscriptionId = Guid.NewGuid().ToString();

    await using (var connection = await source.OpenConnectionAsync())
    await using (var seed = new NpgsqlCommand(
      "INSERT INTO camera_setup (camera_id, server_id) VALUES ($1::uuid, 'test-server');", connection))
    {
      seed.Parameters.AddWithValue(cameraId);
      await seed.ExecuteNonQueryAsync();
    }

    try
    {
      var subscription = Subscription(subscriptionId, cameraId);

      var (first, firstError) = await storage.UpsertAsync(subscription, default);
      Assert.Null(firstError);
      Assert.Equal(1, first!.Version);

      var (second, secondError) = await storage.UpsertAsync(first, default);
      Assert.Null(secondError);
      Assert.Equal(2, second!.Version);
      Assert.Equal(2, (await storage.GetAsync(subscriptionId, default))!.Version);

      var listed = await storage.ListAsync(default);
      var stored = Assert.Single(listed, x => x.Id == subscriptionId);
      Assert.Equal(subscription.Name, stored.Name);
      // jsonb does not keep key order or spacing, so the comparison is by value.
      Assert.True(System.Text.Json.Nodes.JsonNode.DeepEquals(
        System.Text.Json.Nodes.JsonNode.Parse(subscription.SourceFilter),
        System.Text.Json.Nodes.JsonNode.Parse(stored.SourceFilter)));
      var action = Assert.Single(stored.Actions);
      Assert.Equal("retain_recording", action.Kind);
      Assert.True(System.Text.Json.Nodes.JsonNode.DeepEquals(
        System.Text.Json.Nodes.JsonNode.Parse(subscription.Actions[0].Params),
        System.Text.Json.Nodes.JsonNode.Parse(action.Params)));

      // A camera that does not exist is refused before anything is written.
      var missing = Subscription(Guid.NewGuid().ToString(), Guid.NewGuid().ToString());
      var (refused, error) = await storage.UpsertAsync(missing, default);
      Assert.Null(refused);
      Assert.Contains("no such camera", error);

      // The same for a bad selector, whatever the camera.
      var broken = Subscription(Guid.NewGuid().ToString(), cameraId);
      broken.SourceFilter = """{"nonsense":[]}""";
      var (_, selectorError) = await storage.UpsertAsync(broken, default);
      Assert.Contains("unknown source_filter key", selectorError);

      Assert.True((await storage.RemoveAsync(subscriptionId, default)).Removed);
      Assert.Null(await storage.GetAsync(subscriptionId, default));
    }
    finally
    {
      await using var connection = await source.OpenConnectionAsync();
      await using var cleanup = new NpgsqlCommand(
        "DELETE FROM camera_setup WHERE camera_id = $1::uuid;", connection);
      cleanup.Parameters.AddWithValue(cameraId);
      await cleanup.ExecuteNonQueryAsync();
    }
  }

  private static EventSubscription Subscription(string id, string cameraId) => new()
  {
    Id = id,
    Name = "demo-sensor-recording",
    Enabled = true,
    EventName = "sensor",
    SourceFilter = "{\"camera_ids\":[\"" + cameraId + "\"],\"props\":{\"contact\":\"closed\"}}",
    ArmedCondition = "any",
    CooldownMs = 500,
    Actions =
    {
      new SubscriptionAction
      {
        Position = 0,
        Kind = "retain_recording",
        Params = "{\"cameras\":[\"" + cameraId + "\"],\"timeout_seconds\":30}",
      },
    },
  };
}
