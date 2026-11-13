using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using VmsMap.Adapter;
using VmsMap.Clients;
using VmsMap.Db;
using VmsMap.Domain;
using VmsMap.Domain.Arming;

namespace VmsMap.Tests;

// The administrator's card for a logical device object: its arming mode and, for a digital input,
// the contact state that raises an alarm. The mode is the map's; a camera's flag and an input's
// alarm follow from it at once.
public class DeviceSettingsServiceTests
{
  private const string CameraId = "3f2504e0-4f89-11d3-9a0c-0305e82c3301";
  private const string Token = "input1";

  private sealed class Catalog : ICameraCatalog
  {
    public Task<CameraInfo?> GetAsync(string cameraId, CancellationToken ct) =>
      Task.FromResult<CameraInfo?>(new CameraInfo(cameraId, "Camera " + cameraId, "srv"));

    public Task<List<CameraInfo>> ListAsync(string? serverId, CancellationToken ct) =>
      Task.FromResult(new List<CameraInfo>());
  }

  private sealed class Configured(bool configured) : IDeviceSettingsSource
  {
    private DeviceSettingsInfo? Row => configured
      ? new DeviceSettingsInfo(
          CameraId, DeviceObjectId.DigitalInputPart, Token, "Door", ArmMode.Inherit, "closed", true)
      : null;

    public Task<IReadOnlyList<DeviceSettingsInfo>> ListAsync(string cameraId, CancellationToken ct) =>
      Task.FromResult<IReadOnlyList<DeviceSettingsInfo>>(Row is null ? [] : [Row!]);

    public Task<DeviceSettingsInfo?> FindAsync(
      string cameraId, string kind, string token, CancellationToken ct) =>
      Task.FromResult(Row);

    public Task<IReadOnlyDictionary<string, IReadOnlyList<DeviceSettingsInfo>>> ListManyAsync(
      IReadOnlyCollection<string> cameraIds, CancellationToken ct) =>
      Task.FromResult<IReadOnlyDictionary<string, IReadOnlyList<DeviceSettingsInfo>>>(
        cameraIds.Distinct(StringComparer.Ordinal).ToDictionary(
          id => id, _ => (IReadOnlyList<DeviceSettingsInfo>)(Row is null ? [] : [Row!]),
          StringComparer.Ordinal));

    public void Invalidate(string cameraId) { }
  }

  private sealed record Fixture(
    VmsMapDbContext Db,
    MapDb Map,
    DeviceSettingsService Service,
    DeviceObjectSettingsStore Store,
    TestCameraState CameraState,
    TestWiring.TestCfgArmingWriter Cfg);

  private static async Task<Fixture> CreateAsync(bool configured = true)
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
    var updates = new MapUpdateService(map, tracks, states);

    var catalog = new Catalog();
    var source = new Configured(configured);
    var nodes = TestWiring.DeviceNodes(db, map, updates, catalog);
    var store = TestWiring.ObjectSettings(db);
    var cameraState = new TestCameraState();
    var cfg = new TestWiring.TestCfgArmingWriter(cameraState);
    var resolver = TestWiring.Arming(db, cameraState, store);
    var reconciler = TestWiring.CameraArming(db, map, cameraState, cfg);
    var sensorAlarms = TestWiring.SensorAlarms(db, states, map, cameraState, store);

    var service = new DeviceSettingsService(
      db, store, resolver, TestWiring.NodeArming(db, map, cfg, cameraState, source), nodes,
      catalog, source, reconciler, sensorAlarms,
      new EventsWriter(db, new EventsService(db, map)),
      NullLogger<DeviceSettingsService>.Instance);

    await nodes.EnsureAsync(
      DeviceObjectId.CameraSource, CameraId, DeviceObjectId.DigitalInputPart, Token, default);

    return new Fixture(db, map, service, store, cameraState, cfg);
  }

  private static DeviceSettingsKey Camera() =>
    new(DeviceSettingsKey.CameraSource, CameraId, DeviceSettingsKey.DevicePart, string.Empty);

  private static DeviceSettingsKey Input() =>
    new(DeviceSettingsKey.CameraSource, CameraId, DeviceSettingsKey.DigitalInputPart, Token);

  [Fact]
  public async Task ACameraModeIsTheMapsAndItsFlagFollows()
  {
    var f = await CreateAsync();

    var view = await f.Service.SetAsync(Camera(), ArmMode.Armed, null, false, "danil", default);

    Assert.NotNull(view);
    Assert.Equal(ArmMode.Armed, view!.Mode);
    Assert.True(f.CameraState.Armed);
    Assert.Contains((CameraId, true), f.Cfg.CameraFlags);
    Assert.Equal(ArmMode.Armed,
      (await f.Store.GetAsync(CameraObjectId.For(CameraId), default)).Mode);
  }

  [Fact]
  public async Task AnInputsAlarmRuleIsTheMapsAndReadsBack()
  {
    var f = await CreateAsync();

    var view = await f.Service.SetAsync(Input(), ArmMode.Inherit, "closed", true, "danil", default);

    Assert.NotNull(view);
    Assert.Equal("closed", view!.TriggerOn);
    Assert.True(view.AlarmEnabled);
    Assert.True(view.Configured);

    var read = await f.Service.GetAsync(Input(), default);
    Assert.Equal("closed", read!.TriggerOn);
    Assert.True(read.AlarmEnabled);
  }

  [Fact]
  public async Task AnUnconfiguredInputIsRefused()
  {
    var f = await CreateAsync(configured: false);

    var refused = await Assert.ThrowsAsync<ArmingException>(
      () => f.Service.SetAsync(Input(), ArmMode.Armed, "closed", true, "danil", default));

    Assert.Equal(ArmingRefusal.NotConfigured, refused.Refusal);
  }

  [Fact]
  public async Task ACameraCarriesNoAlarmRule()
  {
    var f = await CreateAsync();

    var refused = await Assert.ThrowsAsync<ArmingException>(
      () => f.Service.SetAsync(Camera(), ArmMode.Armed, "closed", true, "danil", default));

    Assert.Equal(ArmingRefusal.InvalidRequest, refused.Refusal);
  }

  [Fact]
  public async Task ARelayIsNotASettingsObject()
  {
    var f = await CreateAsync();

    var key = new DeviceSettingsKey(
      DeviceSettingsKey.CameraSource, CameraId, DeviceSettingsKey.RelayPart, "lamp");
    var refused = await Assert.ThrowsAsync<ArmingException>(
      () => f.Service.SetAsync(key, ArmMode.Inherit, null, false, "danil", default));

    Assert.Equal(ArmingRefusal.InvalidRequest, refused.Refusal);
  }

  [Fact]
  public async Task AnAlarmNeedsTheContactStateItFollows()
  {
    var f = await CreateAsync();

    var refused = await Assert.ThrowsAsync<ArmingException>(
      () => f.Service.SetAsync(Input(), ArmMode.Inherit, null, true, "danil", default));

    Assert.Equal(ArmingRefusal.InvalidRequest, refused.Refusal);
  }

  [Fact]
  public async Task TheOperatorSwitchWritesTheModeAndTheFlag()
  {
    var f = await CreateAsync();

    var view = await f.Service.SetArmingAsync(Camera(), ArmMode.Armed, "danil", default);

    Assert.True(view!.Arming!.Armed);
    Assert.Equal(ArmMode.Armed,
      (await f.Store.GetAsync(CameraObjectId.For(CameraId), default)).Mode);
    Assert.Single(await f.Db.Events.ToListAsync());
  }

  [Fact]
  public async Task AnInputWithNoNodeCannotBeSwitchedByItsSource()
  {
    var f = await CreateAsync();

    var key = new DeviceSettingsKey(
      DeviceObjectId.CameraSource, CameraId, DeviceObjectId.DigitalInputPart, "never-reported");
    var refused = await Assert.ThrowsAsync<ArmingException>(
      () => f.Service.SetArmingAsync(key, ArmMode.Armed, "danil", default));

    Assert.Equal(ArmingRefusal.NotFound, refused.Refusal);
  }
}
