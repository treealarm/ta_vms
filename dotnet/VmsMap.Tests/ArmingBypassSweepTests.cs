using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using VmsMap.Adapter;
using VmsMap.Clients;
using VmsMap.Db;
using VmsMap.Domain;
using VmsMap.Domain.Arming;
using VmsMap.Outbox;

namespace VmsMap.Tests;

// The sweep that ends a timed exclusion: it restores the own mode the node had before the bypass,
// journals the automatic action by `system`, and asks the subscribers to re-check the sensors.
public class ArmingBypassSweepTests
{
  private const string CameraId = "3f2504e0-4f89-11d3-9a0c-0305e82c3301";
  private const string Token = "input1";
  private const string FloorName = "floor";
  private const string BuildingName = "building";
  private const string OperatorName = "danil";
  private const string ClosedContact = "SENSOR_CLOSED";

  private sealed class TestClock(DateTime utcNow) : TimeProvider
  {
    public DateTime UtcNow { get; set; } = utcNow;

    public override DateTimeOffset GetUtcNow() => new(UtcNow, TimeSpan.Zero);
  }

  /// <summary>VmsCfg's view of the configured input, which the node facade requires before a write.</summary>
  private sealed class ConfiguredInput : IDeviceSettingsSource
  {
    private static DeviceSettingsInfo Info(string cameraId) =>
      new(cameraId, DeviceObjectId.DigitalInputPart, Token, "door",
          ArmMode.Inherit, "closed", true);

    public Task<IReadOnlyList<DeviceSettingsInfo>> ListAsync(
      string cameraId, CancellationToken ct) =>
      Task.FromResult<IReadOnlyList<DeviceSettingsInfo>>([Info(cameraId)]);

    public Task<DeviceSettingsInfo?> FindAsync(
      string cameraId, string kind, string token, CancellationToken ct) =>
      Task.FromResult<DeviceSettingsInfo?>(Info(cameraId));

    public Task<IReadOnlyDictionary<string, IReadOnlyList<DeviceSettingsInfo>>> ListManyAsync(
      IReadOnlyCollection<string> cameraIds, CancellationToken ct) =>
      Task.FromResult<IReadOnlyDictionary<string, IReadOnlyList<DeviceSettingsInfo>>>(
        cameraIds.Distinct(StringComparer.Ordinal).ToDictionary(
          id => id, id => (IReadOnlyList<DeviceSettingsInfo>)[Info(id)], StringComparer.Ordinal));

    public void Invalidate(string cameraId) { }
  }

  private sealed record Fixture(
    VmsMapDbContext Db,
    MapDb Map,
    DeviceNodeService Nodes,
    NodeArmingService Arming,
    DeviceObjectSettingsStore Settings,
    TestCameraState CameraState,
    TestWiring.TestCfgArmingWriter Cfg,
    TestClock Clock);

  private static async Task<Fixture> CreateAsync(bool cameraArmed = false)
  {
    var options = new DbContextOptionsBuilder<VmsMapDbContext>()
      .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
      .Options;

    var db = new VmsMapDbContext(options);
    var map = new MapDb(db);
    var stateService = new StateService(db);
    var states = new StatesUpdateService(stateService, map, db);
    var levels = new LevelService(db);
    var tracks = new TracksUpdateService(map, new TrackService(db, levels), new GeoService(db, levels));
    var updates = new MapUpdateService(db, map, tracks, states);

    var nodes = TestWiring.DeviceNodes(db, map, updates);
    var settings = TestWiring.ObjectSettings(db);
    var cameraState = new TestCameraState(cameraArmed);
    var cfg = new TestWiring.TestCfgArmingWriter(cameraState);
    var clock = new TestClock(new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc));
    var arming = TestWiring.NodeArming(
      db, map, cfg, cameraState, new ConfiguredInput(), clock);

    return new Fixture(db, map, nodes, arming, settings, cameraState, cfg, clock);
  }

  private static async Task<BaseMarkerDTO> NodeAsync(
    MapDb map, string name, string? parentId = null, string? ownerId = null)
  {
    var node = new BaseMarkerDTO
    {
      id = Guid.NewGuid().ToString(), name = name, parent_id = parentId, owner_id = ownerId,
    };
    await map.UpdateHierarchyAsync([node]);
    return node;
  }

  private static async Task<BaseMarkerDTO> SpaceAsync(
    Fixture f, string name, string? parentId = null)
  {
    var node = await NodeAsync(f.Map, name, parentId);
    f.Db.Spaces.Add(new DBSpace { object_id = Guid.Parse(node.id!), kind = SpaceKinds.Map });
    await f.Db.SaveChangesAsync();
    return node;
  }

  private static async Task<DBObjectArming?> StoredContainerAsync(Fixture f, string nodeId)
  {
    var id = Guid.Parse(nodeId);
    return await f.Db.ObjectArming.AsNoTracking()
      .FirstOrDefaultAsync(a => a.object_id == id);
  }

  [Fact]
  public async Task AFutureDeadlineIsLeftAlone()
  {
    var f = await CreateAsync();
    var space = await SpaceAsync(f, FloorName);
    await f.Arming.SetAsync(
      space.id!, ArmMode.Disarmed, OperatorName, default, f.Clock.UtcNow.AddMinutes(30));

    Assert.False(await f.Arming.ExpireBypassAsync(space.id!, default));

    var row = await StoredContainerAsync(f, space.id!);
    Assert.NotNull(row);
    Assert.NotNull(row!.bypass_until);
  }

  [Fact]
  public async Task AnExpiredFloorFollowingItsParentRemovesTheRow()
  {
    var f = await CreateAsync();
    var building = await SpaceAsync(f, BuildingName);
    var floor = await SpaceAsync(f, FloorName, building.id);
    await f.Arming.SetAsync(building.id!, ArmMode.Armed, OperatorName, default);

    // A plain disarm first, then the deadline: the timer-only change queues no re-check.
    await f.Arming.SetAsync(floor.id!, ArmMode.Disarmed, OperatorName, default);
    var queued = (await f.Db.MapOutbox.ToListAsync()).Count;
    await f.Arming.SetAsync(
      floor.id!, ArmMode.Disarmed, OperatorName, default, f.Clock.UtcNow.AddMinutes(10));
    Assert.Equal(queued, (await f.Db.MapOutbox.ToListAsync()).Count);

    f.Clock.UtcNow = f.Clock.UtcNow.AddMinutes(20);
    Assert.True(await f.Arming.ExpireBypassAsync(floor.id!, default));

    Assert.Null(await StoredContainerAsync(f, floor.id!));
    Assert.Equal(queued + 1, (await f.Db.MapOutbox.ToListAsync()).Count);

    var entry = (await f.Db.Events.ToListAsync())
      .Where(e => e.object_id == Guid.Parse(floor.id!))
      .OrderByDescending(e => e.timestamp)
      .First();
    Assert.Equal(NodeArmingService.SystemActor, entry.param0);
    Assert.Equal(ArmMode.Inherit, entry.param1);
    Assert.Contains(entry.extra_props, p => p.prop_name == "expired");
  }

  [Fact]
  public async Task AnExpiredArmedRoomComesBackArmed()
  {
    var f = await CreateAsync();
    var space = await SpaceAsync(f, FloorName);
    await f.Arming.SetAsync(space.id!, ArmMode.Armed, OperatorName, default);
    await f.Arming.SetAsync(
      space.id!, ArmMode.Disarmed, OperatorName, default, f.Clock.UtcNow.AddMinutes(10));

    f.Clock.UtcNow = f.Clock.UtcNow.AddMinutes(20);
    Assert.True(await f.Arming.ExpireBypassAsync(space.id!, default));

    var row = await StoredContainerAsync(f, space.id!);
    Assert.True(row!.armed);
    Assert.Null(row.bypass_until);
    Assert.Null(row.restore_mode);

    var expiry = (await f.Db.Events.ToListAsync())
      .Where(e => e.object_id == Guid.Parse(space.id!))
      .OrderByDescending(e => e.timestamp)
      .First();
    Assert.Equal(ArmMode.Armed, expiry.param1);
    Assert.Contains(expiry.extra_props, p => p.prop_name == "expired");
  }

  [Fact]
  public async Task AnExpiredCameraRestoresItsModeAndFlag()
  {
    var f = await CreateAsync();
    // The input's node brings the camera's node with it; the camera id is the canonical node.
    await f.Nodes.EnsureAsync(
      DeviceObjectId.CameraSource, CameraId, DeviceObjectId.DigitalInputPart, Token, default);
    var camera = CameraObjectId.For(CameraId);

    // The camera was explicitly on guard; a timed disarm must bring that back, and the flag with
    // it — the same derivation every other write uses.
    await f.Arming.SetAsync(camera, ArmMode.Armed, OperatorName, default);
    await f.Arming.SetAsync(
      camera, ArmMode.Disarmed, OperatorName, default, f.Clock.UtcNow.AddMinutes(10));

    f.Clock.UtcNow = f.Clock.UtcNow.AddMinutes(20);
    Assert.True(await f.Arming.ExpireBypassAsync(camera, default));

    var stored = await f.Settings.GetAsync(camera, default);
    Assert.Equal(ArmMode.Armed, stored.Mode);
    Assert.Null(stored.BypassUntil);
    Assert.Contains((CameraId, true), f.Cfg.CameraFlags);

    var expiry = (await f.Db.Events.ToListAsync())
      .Where(e => e.object_id == Guid.Parse(camera))
      .OrderByDescending(e => e.timestamp)
      .First();
    Assert.Equal(NodeArmingService.SystemActor, expiry.param0);
    Assert.Equal(ArmMode.Armed, expiry.param1);
    Assert.Contains(expiry.extra_props, p => p.prop_name == "expired");
  }

  [Fact]
  public async Task AnExpiredInputRaisesForItsLiveContact()
  {
    var f = await CreateAsync(cameraArmed: true);
    var input = (await f.Nodes.EnsureAsync(
      DeviceObjectId.CameraSource, CameraId, DeviceObjectId.DigitalInputPart, Token, default))!;
    await f.Settings.SetAsync(input, ArmMode.Inherit, "closed", true, default);
    await f.Arming.SetAsync(
      input, ArmMode.Disarmed, OperatorName, default, f.Clock.UtcNow.AddMinutes(10));
    f.Db.ObjectStates.Add(new DBObjectState
    {
      id = Guid.Parse(input),
      timestamp = f.Clock.UtcNow,
      states = [new DBObjectStateValue
      {
        id = Guid.NewGuid(), object_id = Guid.Parse(input), state = ClosedContact,
      }],
    });
    await f.Db.SaveChangesAsync();
    Assert.Null(await f.Db.Alarms.AsNoTracking()
      .FirstOrDefaultAsync(a => a.status != AlarmStatus.Closed));

    f.Clock.UtcNow = f.Clock.UtcNow.AddMinutes(20);
    Assert.True(await f.Arming.ExpireBypassAsync(input, default));

    var alarm = await f.Db.Alarms.AsNoTracking()
      .FirstOrDefaultAsync(a => a.status != AlarmStatus.Closed);
    Assert.NotNull(alarm);
    Assert.Equal(AlarmSource.Sensor, alarm!.source);
  }

  [Fact]
  public async Task AnOperatorChangeLeavesNothingToExpire()
  {
    var f = await CreateAsync();
    var space = await SpaceAsync(f, FloorName);
    await f.Arming.SetAsync(
      space.id!, ArmMode.Disarmed, OperatorName, default, f.Clock.UtcNow.AddMinutes(10));
    await f.Arming.SetAsync(space.id!, ArmMode.Armed, OperatorName, default);

    f.Clock.UtcNow = f.Clock.UtcNow.AddMinutes(20);
    Assert.False(await f.Arming.ExpireBypassAsync(space.id!, default));

    var row = await StoredContainerAsync(f, space.id!);
    Assert.True(row!.armed);
  }
}
