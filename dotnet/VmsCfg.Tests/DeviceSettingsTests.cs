using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Npgsql;
using Vms.Cfg;
using VmsDb;

namespace VmsCfg.Tests;

// The rules are pure, so they are checked without a database. What a row actually does — the
// upsert, the constraint, the cascade from camera_setup — needs PostgreSQL and runs only when
// VMS_CFG_TEST_DSN points at a scratch database.
public class DeviceSettingsRulesTests
{
  private static DeviceSettings Input(
    string armMode = DeviceSettingsRules.Inherit,
    string triggerOn = "",
    bool alarm = false,
    string kind = DeviceSettingsRules.DigitalInput) => new()
  {
    CameraId = Guid.NewGuid().ToString(),
    Kind = kind,
    Token = "input1",
    ArmMode = armMode,
    TriggerOn = triggerOn,
    AlarmEnabled = alarm,
  };

  [Fact]
  public void AnInputWithNoAlarmIsValid()
  {
    Assert.Null(DeviceSettingsRules.Validate(Input()));
  }

  [Fact]
  public void AnArmedInputThatAlarmsOnAClosedContactIsValid()
  {
    Assert.Null(DeviceSettingsRules.Validate(
      Input(DeviceSettingsRules.Armed, DeviceSettingsRules.TriggerClosed, alarm: true)));
  }

  [Fact]
  public void AnAlarmWithoutAContactIsRefused()
  {
    Assert.NotNull(DeviceSettingsRules.Validate(Input(alarm: true)));
  }

  [Fact]
  public void ARelayCannotCarryArmingOrAlarmSettings()
  {
    var relay = Input(DeviceSettingsRules.Armed, kind: DeviceSettingsRules.Relay);
    Assert.NotNull(DeviceSettingsRules.Validate(relay));
  }

  [Fact]
  public void ARelayWithTheDefaultsIsValid()
  {
    var relay = Input(kind: DeviceSettingsRules.Relay);
    Assert.Null(DeviceSettingsRules.Validate(relay));
  }

  [Theory]
  [InlineData("")]
  [InlineData("bistable")]
  public void AnUnknownModeIsRefused(string kind)
  {
    Assert.NotNull(DeviceSettingsRules.Validate(Input(kind: kind)));
  }

  [Fact]
  public void AnUnknownArmModeIsRefused()
  {
    Assert.NotNull(DeviceSettingsRules.Validate(Input("on")));
  }

  [Fact]
  public void AnUnknownTriggerIsRefused()
  {
    Assert.NotNull(DeviceSettingsRules.Validate(
      Input(DeviceSettingsRules.Inherit, "momentary", alarm: true)));
  }

  [Fact]
  public void AMalformedCameraIdIsRefused()
  {
    var settings = Input();
    settings.CameraId = "cam1";
    Assert.NotNull(DeviceSettingsRules.Validate(settings));
  }

}

// The part of the table that only PostgreSQL can prove: the migration, the idempotent upsert, the
// check constraint, and that deleting a camera takes its settings with it.
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
  public async Task SavingTheSamePartTwiceUpdatesItAndLeavesOneRow()
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
      ArmMode = DeviceSettingsRules.Inherit,
    }, default);

    await o.Storage.SetAsync(new DeviceSettings
    {
      CameraId = o.CameraId,
      Kind = DeviceSettingsRules.DigitalInput,
      Token = "input1",
      Name = "Rear door",
      ArmMode = DeviceSettingsRules.Armed,
      TriggerOn = DeviceSettingsRules.TriggerClosed,
      AlarmEnabled = true,
    }, default);

    var rows = await o.Storage.ListAsync(o.CameraId, default);

    var row = Assert.Single(rows);
    Assert.Equal("Rear door", row.Name);
    Assert.Equal(DeviceSettingsRules.Armed, row.ArmMode);
    Assert.Equal(DeviceSettingsRules.TriggerClosed, row.TriggerOn);
    Assert.True(row.AlarmEnabled);
  }

  [Fact]
  public async Task TheDatabaseRefusesAnAlarmWithoutATrigger()
  {
    var opened = await OpenAsync();
    if (opened is not { } o)
      return;

    await using var source = o.Source;

    await Assert.ThrowsAsync<PostgresException>(() => o.Storage.SetAsync(new DeviceSettings
    {
      CameraId = o.CameraId,
      Kind = DeviceSettingsRules.DigitalInput,
      Token = "input1",
      ArmMode = DeviceSettingsRules.Inherit,
      AlarmEnabled = true,
    }, default));
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
        ArmMode = DeviceSettingsRules.Inherit,
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
      ArmMode = DeviceSettingsRules.Inherit,
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
