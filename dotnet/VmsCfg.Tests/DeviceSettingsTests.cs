using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Npgsql;
using Vms.Cfg;
using VmsDb;

namespace VmsCfg.Tests;

// The rules are pure, so they are checked without a database. They cover the part's identity only:
// what it does — its arming and its contact rule — is the map's and is not written here.
public class DeviceSettingsRulesTests
{
  private static DeviceSettings Input(string kind = DeviceSettingsRules.DigitalInput) => new()
  {
    CameraId = Guid.NewGuid().ToString(),
    Kind = kind,
    Token = "input1",
    Name = "Door contact",
  };

  [Fact]
  public void AConfiguredPartIsValid()
  {
    Assert.Null(DeviceSettingsRules.Validate(Input()));
  }

  [Theory]
  [InlineData("")]
  [InlineData("bistable")]
  public void AnUnknownKindIsRefused(string kind)
  {
    Assert.NotNull(DeviceSettingsRules.Validate(Input(kind: kind)));
  }

  [Fact]
  public void AMalformedCameraIdIsRefused()
  {
    var settings = Input();
    settings.CameraId = "cam1";
    Assert.NotNull(DeviceSettingsRules.Validate(settings));
  }

  [Fact]
  public void AnEmptyTokenIsRefused()
  {
    var settings = Input();
    settings.Token = " ";
    Assert.NotNull(DeviceSettingsRules.Validate(settings));
  }
}

// The part of the table that only PostgreSQL can prove: the migration, the idempotent upsert, the
// cascade from camera_setup, and that a write here touches the name alone.
public class DeviceSettingsPostgresTests
{
  private static readonly string? Dsn = Environment.GetEnvironmentVariable("VMS_CFG_TEST_DSN");

  private static async Task<(NpgsqlDataSource Source, DeviceStorage Storage, string CameraId)?>
    OpenAsync()
  {
    if (string.IsNullOrWhiteSpace(Dsn))
      return null;

    await using (var migrationSource = new NpgsqlDataSourceBuilder(Dsn).Build())
    {
      await new VmsDbMigrator(
        migrationSource, "VmsCfgTest", AppContext.BaseDirectory, "hashtext('ta_vms_schema_migrations')")
        .MigrateAsync();
    }

    var source = new NpgsqlDataSourceBuilder(Dsn).Build();
    var options = new DbContextOptionsBuilder<VmsCfgDbContext>()
      .UseNpgsql(source)
      .Options;
    var storage = new DeviceStorage(new PooledDbContextFactory<VmsCfgDbContext>(options));

    var cameraId = Guid.NewGuid().ToString();
    await using (var command = source.CreateCommand(
      "INSERT INTO camera_setup (camera_id, server_id, name) VALUES ($1::uuid, 'srv1', 'Front door');"))
    {
      command.Parameters.AddWithValue(cameraId);
      await command.ExecuteNonQueryAsync();
    }

    return (source, storage, cameraId);
  }

  [Fact]
  public async Task SavingTheSamePartTwiceUpdatesItsNameAndLeavesOneRow()
  {
    var opened = await OpenAsync();
    if (opened is not { } o)
      return;

    await using var source = o.Source;

    await o.Storage.SetAsync(new DeviceSettings
    {
      CameraId = o.CameraId,
      Kind = DeviceSettingsRules.DigitalInput,
      Token = "input1",
      Name = "Door contact",
    }, default);

    // A caller that still sends the behaviour must not have it stored: it is the map's now.
    await o.Storage.SetAsync(new DeviceSettings
    {
      CameraId = o.CameraId,
      Kind = DeviceSettingsRules.DigitalInput,
      Token = "input1",
      Name = "Rear door",
      ArmMode = "armed",
      TriggerOn = "closed",
      AlarmEnabled = true,
    }, default);

    var row = Assert.Single(await o.Storage.ListAsync(o.CameraId, default));
    Assert.Equal("Rear door", row.Name);
    Assert.Equal(CameraArmingRules.Inherit, row.ArmMode);
    Assert.Empty(row.TriggerOn);
    Assert.False(row.AlarmEnabled);
  }

  [Fact]
  public async Task ANameWriteLeavesTheLegacyColumnsAnOlderReleaseWrote()
  {
    // Release D1 reads the old columns once, into the map. Until that import has run, a name edit
    // must not erase what an older release stored there.
    var opened = await OpenAsync();
    if (opened is not { } o)
      return;

    await using var source = o.Source;
    await using (var command = source.CreateCommand("""
      INSERT INTO device_settings (camera_id, kind, token, name, arm_mode, trigger_on, alarm_enabled)
      VALUES ($1::uuid, 'digital_input', 'input1', 'Door contact', 'armed', 'closed', true);
      """))
    {
      command.Parameters.AddWithValue(o.CameraId);
      await command.ExecuteNonQueryAsync();
    }

    await o.Storage.SetAsync(new DeviceSettings
    {
      CameraId = o.CameraId,
      Kind = DeviceSettingsRules.DigitalInput,
      Token = "input1",
      Name = "Rear door",
    }, default);

    var row = Assert.Single(await o.Storage.ListAsync(o.CameraId, default));
    Assert.Equal("Rear door", row.Name);
    Assert.Equal("armed", row.ArmMode);
    Assert.Equal("closed", row.TriggerOn);
    Assert.True(row.AlarmEnabled);
  }

  [Fact]
  public async Task TheBulkReadAnswersEachCamerasConfiguredPartsInOneQuery()
  {
    var opened = await OpenAsync();
    if (opened is not { } o)
      return;

    await using var source = o.Source;

    var otherCamera = Guid.NewGuid().ToString();
    await using (var command = source.CreateCommand(
      "INSERT INTO camera_setup (camera_id, server_id, name) VALUES ($1::uuid, 'srv1', 'Yard');"))
    {
      command.Parameters.AddWithValue(otherCamera);
      await command.ExecuteNonQueryAsync();
    }

    foreach (var (cameraId, token, name) in new[]
             {
               (o.CameraId, "input1", "Door"),
               (o.CameraId, "relay1", "Lamp"),
               (otherCamera, "input1", "Gate"),
             })
    {
      await o.Storage.SetAsync(new DeviceSettings
      {
        CameraId = cameraId,
        Kind = token.StartsWith("relay", StringComparison.Ordinal)
          ? DeviceSettingsRules.Relay
          : DeviceSettingsRules.DigitalInput,
        Token = token,
        Name = name,
      }, default);
    }

    // A camera with nothing configured is asked for too: its absence is the answer, not a miss.
    var unconfigured = Guid.NewGuid().ToString();
    var rows = await o.Storage.ListManyAsync(
      [o.CameraId, otherCamera, unconfigured, "not-a-uuid"], default);

    Assert.Equal(3, rows.Count);
    Assert.Equal(2, rows.Count(r => r.CameraId == o.CameraId));
    Assert.Equal(1, rows.Count(r => r.CameraId == otherCamera));
    Assert.DoesNotContain(rows, r => r.CameraId == unconfigured);
  }

  [Fact]
  public async Task TheBulkReadOfNoCamerasAsksTheDatabaseNothing()
  {
    var opened = await OpenAsync();
    if (opened is not { } o)
      return;

    await using var source = o.Source;

    Assert.Empty(await o.Storage.ListManyAsync([], default));
    Assert.Empty(await o.Storage.ListManyAsync(["not-a-uuid"], default));
  }

  [Fact]
  public async Task DeletingTheCameraTakesItsSettings()
  {
    var opened = await OpenAsync();
    if (opened is not { } o)
      return;

    await using var source = o.Source;
    await o.Storage.SetAsync(new DeviceSettings
    {
      CameraId = o.CameraId,
      Kind = DeviceSettingsRules.Relay,
      Token = "relay1",
      Name = "Lamp",
    }, default);

    await using (var command = source.CreateCommand(
      "DELETE FROM camera_setup WHERE camera_id = $1::uuid;"))
    {
      command.Parameters.AddWithValue(o.CameraId);
      await command.ExecuteNonQueryAsync();
    }

    Assert.Empty(await o.Storage.ListAsync(o.CameraId, default));
  }
}
