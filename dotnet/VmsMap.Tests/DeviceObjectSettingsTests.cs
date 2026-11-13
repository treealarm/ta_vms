using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using VmsMap.Db;
using VmsMap.Domain.Arming;

namespace VmsMap.Tests;

// The map's own copy of a device object's behaviour: the mode its arming is decided with and the
// contact state that raises an alarm. An absent row is the default, so the store answers every id
// it is asked about.
public class DeviceObjectSettingsTests
{
  private static VmsMapDbContext Create()
  {
    var options = new DbContextOptionsBuilder<VmsMapDbContext>()
      .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
      .Options;
    return new VmsMapDbContext(options);
  }

  private static string NodeId() => Guid.NewGuid().ToString();

  [Fact]
  public async Task AnObjectWithNoRowFollowsItsZoneAndRaisesNoAlarm()
  {
    await using var db = Create();
    var store = new DeviceObjectSettingsStore(db);

    var settings = await store.GetAsync(NodeId(), default);

    Assert.Equal(ArmMode.Inherit, settings.Mode);
    Assert.Null(settings.TriggerOn);
    Assert.False(settings.AlarmEnabled);
  }

  [Fact]
  public async Task WhatIsWrittenIsReadBack()
  {
    await using var db = Create();
    var store = new DeviceObjectSettingsStore(db);
    var id = NodeId();

    await store.SetAsync(id, ArmMode.Disarmed, "closed", true, default);

    var settings = await store.GetAsync(id, default);
    Assert.Equal(ArmMode.Disarmed, settings.Mode);
    Assert.Equal("closed", settings.TriggerOn);
    Assert.True(settings.AlarmEnabled);
  }

  [Fact]
  public async Task WritingTwiceUpdatesTheSameRow()
  {
    await using var db = Create();
    var store = new DeviceObjectSettingsStore(db);
    var id = NodeId();

    await store.SetAsync(id, ArmMode.Armed, null, false, default);
    await store.SetAsync(id, ArmMode.Inherit, "open", true, default);

    Assert.Equal(1, await db.DeviceObjectSettings.CountAsync());
    var settings = await store.GetAsync(id, default);
    Assert.Equal(ArmMode.Inherit, settings.Mode);
    Assert.Equal("open", settings.TriggerOn);
    Assert.True(settings.AlarmEnabled);
  }

  [Fact]
  public async Task APageAnswersEveryIdWithItsRowOrDefaults()
  {
    await using var db = Create();
    var store = new DeviceObjectSettingsStore(db);
    var configured = NodeId();
    var unconfigured = NodeId();
    await store.SetAsync(configured, ArmMode.Armed, null, false, default);

    var page = await store.GetManyAsync([configured, unconfigured], default);

    Assert.Equal(2, page.Count);
    Assert.Equal(ArmMode.Armed, page[configured].Mode);
    Assert.Equal(ArmMode.Inherit, page[unconfigured].Mode);
  }

  [Fact]
  public async Task AnIdThatIsNotAnObjectIdHasNoSettings()
  {
    await using var db = Create();
    var store = new DeviceObjectSettingsStore(db);

    var settings = await store.GetAsync("sim-master", default);
    Assert.Equal(ArmMode.Inherit, settings.Mode);

    await Assert.ThrowsAsync<ArgumentException>(
      () => store.SetAsync("sim-master", ArmMode.Armed, null, false, default));
  }
}
