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

    public Task<IReadOnlyDictionary<string, IReadOnlyList<DeviceSettingsInfo>>> ListManyAsync(
      IReadOnlyCollection<string> cameraIds, CancellationToken ct) =>
      Task.FromResult<IReadOnlyDictionary<string, IReadOnlyList<DeviceSettingsInfo>>>(
        cameraIds.Distinct(StringComparer.Ordinal).ToDictionary(
          id => id,
          id => (IReadOnlyList<DeviceSettingsInfo>)Entries
            .Where(e => e.CameraId == id).ToList(),
          StringComparer.Ordinal));

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
      // The resolver reads the same settings the rule does: an input's explicit mode must win over
      // the zone in this path too.
      new SensorAlarmService(db, source, TestWiring.Arming(db, arming, source), alarms, map,
        NullLogger<SensorAlarmService>.Instance),
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
    var sensor = new SensorAlarmService(s.Db, off, TestWiring.Arming(s.Db, s.Arming), s.Alarms, s.Map,
        NullLogger<SensorAlarmService>.Instance);

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

  // ── A zone ────────────────────────────────────────────────────────────────────────────────────

  /// <summary>A space a camera's reference replica stands in, optionally armed. Arms the zone the
  /// way the facade writes it, and answers the space's node id.</summary>
  private static async Task<string> ZoneAsync(Services s, bool armed)
  {
    var space = new BaseMarkerDTO { id = Guid.NewGuid().ToString(), name = "Floor" };
    await s.Map.UpdateHierarchyAsync([space]);

    s.Db.Spaces.Add(new DBSpace { object_id = Guid.Parse(space.id!), kind = SpaceKinds.Map });

    var replica = new BaseMarkerDTO
    {
      id = Guid.NewGuid().ToString(),
      name = "Camera on the plan",
      parent_id = space.id,
      owner_id = CameraObjectId.For(s.CameraId),
    };
    await s.Map.UpdateHierarchyAsync([replica]);
    s.Db.CameraPlacements.Add(new DBCameraPlacement
    {
      object_id = Guid.Parse(replica.id!),
      camera_id = s.CameraId,
      is_reference = true,
    });

    if (armed)
    {
      s.Db.ObjectArming.Add(new DBObjectArming
      {
        object_id = Guid.Parse(space.id!),
        armed = true,
      });
    }

    await s.Db.SaveChangesAsync();
    return space.id!;
  }

  private static async Task StoreContactAsync(Services s, Guid input, string state)
  {
    await s.Db.ObjectStates.AddAsync(new DBObjectState
    {
      id = input,
      timestamp = DateTime.UtcNow,
      states = [new DBObjectStateValue { id = Guid.NewGuid(), object_id = input, state = state }],
    });
    await s.Db.SaveChangesAsync();
  }

  [Fact]
  public async Task ArmingAZoneRaisesForAContactAlreadyInItsTriggerPosition()
  {
    var s = Create(AlarmOn(TestCameras.One), armed: false);
    var input = await DeviceAsync(s);
    await StoreContactAsync(s, input, SensorAlarmService.SensorClosed);

    // The zone is written after the contact closed while nothing was armed: no alarm so far.
    await s.Sensor.ReconcileAsync(input, s.CameraId, DeviceObjectId.DigitalInputPart, Token, "closed");
    Assert.Null(await Open(s.Db));

    var space = await ZoneAsync(s, armed: true);
    await s.Sensor.ReconcileSpaceAsync(space, default);

    var alarm = await Open(s.Db);
    Assert.NotNull(alarm);
    Assert.Equal(input, alarm!.object_id);
    Assert.Equal(SensorAlarmService.ArmedWithActiveCondition,
      (await s.Db.Events.SingleAsync(e => e.event_name == AlarmStore.RaisedEvent)).param1);
  }

  [Fact]
  public async Task AZoneRaisesNothingForAContactAtRest()
  {
    var s = Create(AlarmOn(TestCameras.One), armed: false);
    var input = await DeviceAsync(s);
    await StoreContactAsync(s, input, SensorAlarmService.SensorOpen);

    var space = await ZoneAsync(s, armed: true);
    await s.Sensor.ReconcileSpaceAsync(space, default);

    Assert.Null(await Open(s.Db));
  }

  [Fact]
  public async Task RepeatingTheZoneRecheckDoesNotDuplicateTheAlarm()
  {
    var s = Create(AlarmOn(TestCameras.One), armed: false);
    var input = await DeviceAsync(s);
    await StoreContactAsync(s, input, SensorAlarmService.SensorClosed);
    var space = await ZoneAsync(s, armed: true);

    await s.Sensor.ReconcileSpaceAsync(space, default);
    await s.Sensor.ReconcileSpaceAsync(space, default);

    Assert.Single(await s.Db.Alarms.Where(a => a.status != AlarmStatus.Closed).ToListAsync());
  }

  [Fact]
  public async Task AnExplicitlyDisarmedInputIgnoresItsArmedZone()
  {
    var settings = new DeviceSettingsInfo(
      TestCameras.One, DeviceObjectId.DigitalInputPart, Token, "Door", "disarmed", "closed", true);
    var s = Create(settings, armed: false);
    var input = await DeviceAsync(s);
    await StoreContactAsync(s, input, SensorAlarmService.SensorClosed);

    var space = await ZoneAsync(s, armed: true);
    await s.Sensor.ReconcileSpaceAsync(space, default);

    Assert.Null(await Open(s.Db));
  }

  [Fact]
  public async Task AReturnedAlarmComesBackWhenTheZoneIsArmedWithTheContactClosedAgain()
  {
    var s = Create(AlarmOn(TestCameras.One), armed: true);
    var input = await DeviceAsync(s);
    await StoreContactAsync(s, input, SensorAlarmService.SensorClosed);

    await s.Sensor.ReconcileAsync(input, s.CameraId, DeviceObjectId.DigitalInputPart, Token, "closed");
    Assert.Equal(AlarmStatus.Active, (await Open(s.Db))!.status);

    // The contact opens: the condition is gone, the row is a returned alarm.
    await s.Sensor.ReconcileAsync(input, s.CameraId, DeviceObjectId.DigitalInputPart, Token, "open");
    Assert.Equal(AlarmStatus.Returned, (await Open(s.Db))!.status);

    // The camera is disarmed while the contact is closed again — nothing to raise — and then a zone
    // is armed over it. The stored contact is the trigger position, so the re-check must fire.
    s.Arming.Armed = false;

    var space = await ZoneAsync(s, armed: true);
    await s.Sensor.ReconcileSpaceAsync(space, default);

    // The returned row is reactivated, not duplicated: the store's dedup key still holds it.
    var alarm = await Open(s.Db);
    Assert.Equal(AlarmStatus.Active, alarm!.status);
    Assert.Single(await s.Db.Alarms.ToListAsync());
  }
}
