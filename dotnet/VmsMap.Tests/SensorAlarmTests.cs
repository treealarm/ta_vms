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

namespace VmsMap.Tests;

// The contact-to-alarm rule: what raises a sensor alarm, what does not, and how a settings change
// or a snapshot repairs a condition that is live while the alarm is missing.
public class SensorAlarmTests
{
  private sealed class Settings(params DeviceSettingsInfo[] entries) : IDeviceSettingsSource
  {
    public List<DeviceSettingsInfo> Entries { get; } = [.. entries];

    public Task<IReadOnlyList<DeviceSettingsInfo>> ListAsync(string cameraId, CancellationToken ct) =>
      Task.FromResult<IReadOnlyList<DeviceSettingsInfo>>(
        Entries.Where(e => e.CameraId == cameraId).ToList());

    public Task<DeviceSettingsInfo?> FindAsync(
      string cameraId, string kind, string token, CancellationToken ct) =>
      Task.FromResult(Entries.FirstOrDefault(e =>
        e.CameraId == cameraId && e.Kind == kind && e.Token == token));

    public void Invalidate(string cameraId) { }
  }

  private sealed record Services(
    VmsMapDbContext Db, MapDb Map, AlarmStore Alarms, SensorAlarmService Sensor,
    TestCameraState Arming, string CameraId, string Token);

  private const string Token = "1";

  private static Services Create(DeviceSettingsInfo? settings = null, bool armed = false)
  {
    var options = new DbContextOptionsBuilder<VmsMapDbContext>()
      .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
      .Options;

    var db = new VmsMapDbContext(options);
    var map = new MapDb(db);
    var stateService = new StateService(db);
    var states = new StatesUpdateService(stateService, map, db);
    var arming = new TestCameraState(armed);
    var source = new Settings(settings is null ? [] : [settings]);
    var alarms = TestWiring.Alarms(db, map, states, arming);

    return new Services(
      db, map, alarms,
      new SensorAlarmService(db, source, TestWiring.Arming(db, arming), alarms),
      arming, TestCameras.One, Token);
  }

  private static async Task<Guid> DeviceAsync(Services s)
  {
    var camera = Guid.Parse(CameraObjectId.For(s.CameraId));
    var input = Guid.Parse(DeviceObjectId.For(
      DeviceObjectId.CameraSource, s.CameraId, DeviceObjectId.DigitalInputPart, s.Token));

    await s.Map.UpdateHierarchyAsync([
      new BaseMarkerDTO { id = camera.ToString(), name = "Camera" },
      new BaseMarkerDTO { id = input.ToString(), parent_id = camera.ToString(), name = "Door" },
    ]);

    s.Db.DeviceNodes.Add(TestCameras.DeviceNode(s.CameraId, camera));
    s.Db.DeviceNodes.Add(new DBDeviceNode
    {
      object_id = input,
      source_kind = DeviceObjectId.CameraSource,
      source_id = s.CameraId,
      part_kind = DeviceObjectId.DigitalInputPart,
      part_token = s.Token,
    });
    await s.Db.SaveChangesAsync();
    return input;
  }

  private static DeviceSettingsInfo AlarmOn(string cameraId, string triggerOn = "closed") =>
    new(cameraId, DeviceObjectId.DigitalInputPart, Token, "Door", "inherit", triggerOn, true);

  private static Task<DBAlarm?> Open(VmsMapDbContext db) =>
    db.Alarms.AsNoTracking().Where(a => a.status != AlarmStatus.Closed)
      .OrderByDescending(a => a.raised_at).FirstOrDefaultAsync();

  [Fact]
  public async Task ATriggeredContactOnAnArmedInputRaises()
  {
    var s = Create(AlarmOn(TestCameras.One), armed: true);
    var input = await DeviceAsync(s);

    await s.Sensor.ReconcileAsync(input, s.CameraId, DeviceObjectId.DigitalInputPart, Token, "closed");

    var alarm = await Open(s.Db);
    Assert.NotNull(alarm);
    Assert.Equal(AlarmSource.Sensor, alarm!.source);
    Assert.Equal(SensorAlarmService.DedupKey(
      DeviceObjectId.CameraSource, s.CameraId, DeviceObjectId.DigitalInputPart, Token),
      alarm.dedup_key);
    Assert.Equal(AlarmSeverity.Warning, alarm.severity);

    var evt = await s.Db.Events.Include(e => e.extra_props)
      .SingleAsync(e => e.event_name == AlarmStore.RaisedEvent);
    Assert.Equal("warning", evt.param0);
    Assert.Equal("closed", evt.param1);
  }

  [Fact]
  public async Task AContactThatIsNotTheTriggerReturnsTheAlarmToNormal()
  {
    var s = Create(AlarmOn(TestCameras.One), armed: true);
    var input = await DeviceAsync(s);

    await s.Sensor.ReconcileAsync(input, s.CameraId, DeviceObjectId.DigitalInputPart, Token, "closed");
    await s.Sensor.ReconcileAsync(input, s.CameraId, DeviceObjectId.DigitalInputPart, Token, "open");

    var alarm = await Open(s.Db);
    Assert.NotNull(alarm);
    Assert.Equal(AlarmStatus.Returned, alarm!.status);
    Assert.False(alarm.condition_active);
  }

  [Fact]
  public async Task AnUnknownPolarityRaisesNothing()
  {
    var s = Create(AlarmOn(TestCameras.One), armed: true);
    var input = await DeviceAsync(s);

    await s.Sensor.ReconcileAsync(input, s.CameraId, DeviceObjectId.DigitalInputPart, Token, null);

    Assert.Null(await Open(s.Db));
  }

  [Fact]
  public async Task ADisarmedInputRaisesNothing()
  {
    var s = Create(AlarmOn(TestCameras.One), armed: false);
    var input = await DeviceAsync(s);

    await s.Sensor.ReconcileAsync(input, s.CameraId, DeviceObjectId.DigitalInputPart, Token, "closed");

    Assert.Null(await Open(s.Db));
  }

  [Fact]
  public async Task ARelayNeverAlarms()
  {
    var s = Create(
      new DeviceSettingsInfo(
        TestCameras.One, DeviceObjectId.RelayPart, Token, "Relay", "inherit", null, false),
      armed: true);
    var input = await DeviceAsync(s);

    await s.Sensor.ReconcileAsync(input, s.CameraId, DeviceObjectId.RelayPart, Token, "closed");

    Assert.Null(await Open(s.Db));
  }

  [Fact]
  public async Task TurningTheAlarmOffReturnsTheConditionToNormal()
  {
    var s = Create(AlarmOn(TestCameras.One), armed: true);
    var input = await DeviceAsync(s);
    await s.Sensor.ReconcileAsync(input, s.CameraId, DeviceObjectId.DigitalInputPart, Token, "closed");

    // The operator saves the input with the alarm off: the open row must not stay as if it were.
    var off = new Settings(new DeviceSettingsInfo(
      TestCameras.One, DeviceObjectId.DigitalInputPart, Token, "Door", "inherit", "closed", false));
    var sensor = new SensorAlarmService(s.Db, off, TestWiring.Arming(s.Db, s.Arming), s.Alarms);

    await sensor.ReconcileAsync(input, s.CameraId, DeviceObjectId.DigitalInputPart, Token, "closed");

    Assert.Equal(AlarmStatus.Returned, (await Open(s.Db))!.status);
  }

  [Fact]
  public async Task ArmingAnInputWhoseContactIsAlreadyTriggeredRaisesImmediately()
  {
    var s = Create(AlarmOn(TestCameras.One), armed: false);
    var input = await DeviceAsync(s);

    // The contact is closed on record while the input is disarmed: no alarm yet.
    await s.Db.ObjectStates.AddAsync(new DBObjectState
    {
      id = input,
      timestamp = DateTime.UtcNow,
      states = [new DBObjectStateValue { id = Guid.NewGuid(), object_id = input, state = SensorAlarmService.SensorClosed }],
    });
    await s.Db.SaveChangesAsync();
    await s.Sensor.ReconcileAsync(input, s.CameraId, DeviceObjectId.DigitalInputPart, Token, "closed");
    Assert.Null(await Open(s.Db));

    // The camera is armed; the reconcile from the settings change finds the condition already true.
    s.Arming.Armed = true;
    await s.Sensor.ReconcileCameraAsync(s.CameraId, default);

    var alarm = await Open(s.Db);
    Assert.NotNull(alarm);
    Assert.Equal(SensorAlarmService.ArmedWithActiveCondition,
      (await s.Db.Events.SingleAsync(e => e.event_name == AlarmStore.RaisedEvent)).param1);
  }

  [Fact]
  public async Task TheRuleRepairsAMissingAlarmAndClearsOneThatNoLongerHolds()
  {
    var s = Create(AlarmOn(TestCameras.One), armed: true);
    var input = await DeviceAsync(s);

    // State on record says closed while no alarm exists: exactly a lost event.
    await s.Db.ObjectStates.AddAsync(new DBObjectState
    {
      id = input,
      timestamp = DateTime.UtcNow,
      states = [new DBObjectStateValue { id = Guid.NewGuid(), object_id = input, state = SensorAlarmService.SensorClosed }],
    });
    await s.Db.SaveChangesAsync();

    await s.Sensor.ReconcileAsync(input, s.CameraId, DeviceObjectId.DigitalInputPart, Token, "closed");
    Assert.NotNull(await Open(s.Db));

    await s.Sensor.ReconcileAsync(input, s.CameraId, DeviceObjectId.DigitalInputPart, Token, "open");
    Assert.Equal(AlarmStatus.Returned, (await Open(s.Db))!.status);
  }
}
