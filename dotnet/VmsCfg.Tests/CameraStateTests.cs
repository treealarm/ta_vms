using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Vms.Cfg;
using VmsDb;

namespace VmsCfg.Tests;

// The rules are pure and always run. What a row actually does — an explicit mode, the derived
// value the map writes, the check that keeps the two from disagreeing — needs PostgreSQL and runs
// only when VMS_CFG_TEST_DSN points at a scratch database.
public class CameraArmingRulesTests
{
  [Theory]
  [InlineData(DeviceSettingsRules.Inherit)]
  [InlineData(DeviceSettingsRules.Armed)]
  [InlineData(DeviceSettingsRules.Disarmed)]
  public void EveryModeIsAccepted(string mode)
  {
    Assert.Null(CameraArmingRules.ValidateArmMode(mode));
  }

  [Theory]
  [InlineData("")]
  [InlineData("on")]
  [InlineData("Armed")]
  public void AnUnknownModeIsRefused(string mode)
  {
    Assert.NotNull(CameraArmingRules.ValidateArmMode(mode));
  }

  [Fact]
  public void ACameraIdThatIsNotAUuidIsRefused()
  {
    Assert.Null(CameraArmingRules.ValidateCameraId(Guid.NewGuid().ToString()));
    Assert.NotNull(CameraArmingRules.ValidateCameraId("sim-master"));
  }
}

public class CameraStatePostgresTests
{
  private static readonly string? Dsn = Environment.GetEnvironmentVariable("VMS_CFG_TEST_DSN");

  private static async Task<(NpgsqlDataSource Source, CameraStorage Storage, string CameraId)?>
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
    var options = new DbContextOptionsBuilder<VmsCfgDbContext>().UseNpgsql(source).Options;
    // The camera state methods touch neither cache: only the database and the log are theirs.
    var storage = new CameraStorage(
      source, new PooledDbContextFactory<VmsCfgDbContext>(options), null!, null!,
      NullLogger<CameraStorage>.Instance);

    var cameraId = Guid.NewGuid().ToString();
    await using (var command = source.CreateCommand(
      "INSERT INTO camera_setup (camera_id, server_id, name) VALUES ($1::uuid, 'srv1', 'Hall');"))
    {
      command.Parameters.AddWithValue(cameraId);
      await command.ExecuteNonQueryAsync();
    }

    return (source, storage, cameraId);
  }

  [Fact]
  public async Task ACameraThatNeverHadAStateRowFollowsItsZone()
  {
    var opened = await OpenAsync();
    if (opened is not { } o)
      return;

    await using var source = o.Source;
    var state = await o.Storage.GetCameraArmStateAsync(o.CameraId, default);

    Assert.False(state.Armed);
    Assert.Equal(DeviceSettingsRules.Inherit, state.ArmMode);
  }

  [Fact]
  public async Task AnOperatorsCommandMakesTheModeExplicit()
  {
    var opened = await OpenAsync();
    if (opened is not { } o)
      return;

    await using var source = o.Source;

    await o.Storage.SetCameraArmedAsync(o.CameraId, true, default);
    var armed = await o.Storage.GetCameraArmStateAsync(o.CameraId, default);
    Assert.True(armed.Armed);
    Assert.Equal(DeviceSettingsRules.Armed, armed.ArmMode);

    await o.Storage.SetCameraArmedAsync(o.CameraId, false, default);
    var disarmed = await o.Storage.GetCameraArmStateAsync(o.CameraId, default);
    Assert.False(disarmed.Armed);
    Assert.Equal(DeviceSettingsRules.Disarmed, disarmed.ArmMode);
  }

  [Fact]
  public async Task TheAdministratorLeavesACameraToItsZoneAndTheMapFillsTheFlagIn()
  {
    var opened = await OpenAsync();
    if (opened is not { } o)
      return;

    await using var source = o.Source;

    await o.Storage.SetCameraArmedAsync(o.CameraId, true, default);
    await o.Storage.SetCameraArmModeAsync(o.CameraId, DeviceSettingsRules.Inherit, default);

    // The flag is left as it was until the map derives it: staying on guard is the safe answer.
    var inherited = await o.Storage.GetCameraArmStateAsync(o.CameraId, default);
    Assert.True(inherited.Armed);
    Assert.Equal(DeviceSettingsRules.Inherit, inherited.ArmMode);

    Assert.True(await o.Storage.SetCameraEffectiveArmedAsync(o.CameraId, false, default));
    var derived = await o.Storage.GetCameraArmStateAsync(o.CameraId, default);
    Assert.False(derived.Armed);
    Assert.Equal(DeviceSettingsRules.Inherit, derived.ArmMode);
  }

  [Fact]
  public async Task TheDerivedWriteCreatesTheRowAndThenStaysQuiet()
  {
    var opened = await OpenAsync();
    if (opened is not { } o)
      return;

    await using var source = o.Source;

    Assert.True(await o.Storage.SetCameraEffectiveArmedAsync(o.CameraId, true, default));
    var state = await o.Storage.GetCameraArmStateAsync(o.CameraId, default);
    Assert.True(state.Armed);
    Assert.Equal(DeviceSettingsRules.Inherit, state.ArmMode);

    Assert.False(await o.Storage.SetCameraEffectiveArmedAsync(o.CameraId, true, default));
  }

  [Fact]
  public async Task TheDerivedWriteCannotOverrideAnExplicitMode()
  {
    var opened = await OpenAsync();
    if (opened is not { } o)
      return;

    await using var source = o.Source;

    await o.Storage.SetCameraArmedAsync(o.CameraId, false, default);

    Assert.False(await o.Storage.SetCameraEffectiveArmedAsync(o.CameraId, true, default));
    var state = await o.Storage.GetCameraArmStateAsync(o.CameraId, default);
    Assert.False(state.Armed);
    Assert.Equal(DeviceSettingsRules.Disarmed, state.ArmMode);
  }

  [Fact]
  public async Task TheDatabaseRefusesAFlagThatContradictsAnExplicitMode()
  {
    var opened = await OpenAsync();
    if (opened is not { } o)
      return;

    await using var source = o.Source;
    await using var command = source.CreateCommand("""
      INSERT INTO camera_states (camera_id, armed, arm_mode)
      VALUES ($1::uuid, false, 'armed');
      """);
    command.Parameters.AddWithValue(o.CameraId);

    var error = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
    Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
  }
}
