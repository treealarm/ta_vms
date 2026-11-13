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
    Assert.Equal(CameraArmingRules.Inherit, state.ArmMode);
  }

  [Fact]
  public async Task TheMapWritesTheFlagWhateverTheLegacyModeSays()
  {
    var opened = await OpenAsync();
    if (opened is not { } o)
      return;

    await using var source = o.Source;

    // A row the previous release left behind: an explicit mode, and the flag that matched it.
    await using (var insert = source.CreateCommand("""
      INSERT INTO camera_states (camera_id, armed, arm_mode) VALUES ($1::uuid, false, 'disarmed');
      """))
    {
      insert.Parameters.AddWithValue(o.CameraId);
      await insert.ExecuteNonQueryAsync();
    }

    // The map has resolved the flag from the mode or the zone; VmsCfg no longer has a say.
    Assert.True(await o.Storage.SetCameraEffectiveArmedAsync(o.CameraId, true, default));
    var state = await o.Storage.GetCameraArmStateAsync(o.CameraId, default);
    Assert.True(state.Armed);
    Assert.Equal("disarmed", state.ArmMode);
    Assert.False(await o.Storage.SetCameraEffectiveArmedAsync(o.CameraId, true, default));
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
    Assert.Equal(CameraArmingRules.Inherit, state.ArmMode);

    Assert.False(await o.Storage.SetCameraEffectiveArmedAsync(o.CameraId, true, default));
  }

}
