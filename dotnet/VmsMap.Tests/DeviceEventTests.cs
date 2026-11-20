using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using VmsMap.Adapter;
using VmsMap.Db;
using VmsMap.Domain;
using VmsMap.Domain.Arming;

namespace VmsMap.Tests;

// What a device says, and what the map makes of it: the state dictionary, the journal event with
// its addressing, and the (epoch, seq) cursor that keeps a redelivery from turning the contact back.
public class DeviceEventTranslatorTests
{
  private const string CameraId = "3f2504e0-4f89-11d3-9a0c-0305e82c3301";

  // The fixtures these tests share: the part's token and the contact value that becomes a state.
  private const string PartToken = "part1";
  private const string OpenContact = "open";

  private static DeviceEventMessage Message(
    string partKind, JsonElement? contact = null, JsonElement? active = null) =>
    new("camera", CameraId, partKind, PartToken, contact, active,
        Previous: null, Initialized: false, DateTimeOffset.UtcNow,
        Epoch: Guid.NewGuid().ToString(), Seq: 1, PolarityKnown: true);

  private static JsonElement Text(string value) => JsonSerializer.SerializeToElement(value);
  private static JsonElement Bool(bool value) => JsonSerializer.SerializeToElement(value);

  [Theory]
  [InlineData(OpenContact, DeviceEventTranslator.SensorOpen)]
  [InlineData("closed", DeviceEventTranslator.SensorClosed)]
  public void AnInputBecomesItsSensorState(string contact, string expected)
  {
    var change = new DeviceEventTranslator().Translate(
      Message(DeviceObjectId.DigitalInputPart, contact: Text(contact)));

    Assert.NotNull(change);
    Assert.Equal(expected, change!.State);
    Assert.Equal("sensor", change.EventName);
    Assert.Equal(contact, change.Param0);
  }

  [Theory]
  [InlineData(true, DeviceEventTranslator.RelayActive, "active")]
  [InlineData(false, DeviceEventTranslator.RelayInactive, "inactive")]
  public void ARelayBecomesItsCoilState(bool active, string expected, string param)
  {
    var change = new DeviceEventTranslator().Translate(
      Message(DeviceObjectId.RelayPart, active: Bool(active)));

    Assert.NotNull(change);
    Assert.Equal(expected, change!.State);
    Assert.Equal("relay", change.EventName);
    Assert.Equal(param, change.Param0);
  }

  [Fact]
  public void AnUnknownContactCarriesNoStateToGuess()
  {
    var change = new DeviceEventTranslator().Translate(
      Message(DeviceObjectId.DigitalInputPart, contact: null));

    Assert.Null(change);
  }

  [Fact]
  public void TheEventAddressesItsSource()
  {
    var change = new DeviceEventTranslator().Translate(
      Message(DeviceObjectId.DigitalInputPart, contact: Text(OpenContact)));

    var props = change!.ExtraProps.ToDictionary(p => p.prop_name!, p => p.str_val);
    Assert.Equal(CameraId, props["camera_id"]);
    Assert.Equal(DeviceObjectId.DigitalInputPart, props["part_kind"]);
    Assert.Equal(PartToken, props["token"]);
    Assert.Equal(OpenContact, props["contact"]);
    Assert.Equal("true", props["polarity_known"]);
    Assert.True(props.ContainsKey("observed_at"));
  }
}

public class DeviceEventWriterTests
{
  private const string CameraId = "3f2504e0-4f89-11d3-9a0c-0305e82c3301";
  private const string Token = "input1";

  // The fixtures these tests share: the contact values, the snapshot's device info, and the empty
  // answer that replaces the stored model.
  private const string OpenContact = "open";
  private const string ClosedContact = "closed";
  private const string CameraSourceKind = "camera";
  private const string ManufacturerName = "OnvifSim";
  private const string ModelName = "Simulated fixed camera";
  private const string FirmwareVersion = "0.1.0";
  private const string EmptyValue = "";

  private sealed record Services(
    VmsMapDbContext Db, DeviceNodeService Nodes, DeviceEventWriter Writer,
    DeviceEventReconciler Reconciler, AlarmStore Alarms, string ObjectId);

  private static async Task<Services> CreateAsync(
    bool armed = false, DBDeviceObjectSettings? settings = null)
  {
    var options = new DbContextOptionsBuilder<VmsMapDbContext>()
      .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
      .Options;

    var db = new VmsMapDbContext(options);
    var map = new MapDb(db);
    var stateService = new StateService(db);
    var statesUpdate = new StatesUpdateService(stateService, map, db);
    var levels = new LevelService(db);
    var tracks = new TracksUpdateService(map, new TrackService(db, levels), new GeoService(db, levels));
    var updates = new MapUpdateService(db, map, tracks, statesUpdate);
    var nodes = TestWiring.DeviceNodes(db, map, updates);

    var arming = new TestCameraState(armed);
    var objectSettings = TestWiring.ObjectSettings(db);
    var alarms = TestWiring.Alarms(db, map, statesUpdate, arming);
    var faults = new DeviceFaultService(db, alarms);
    var sensorAlarms = new SensorAlarmService(
      db, objectSettings, TestWiring.Arming(db, arming, objectSettings), alarms, map,
      NullLogger<SensorAlarmService>.Instance);

    var stateWriter = new ObjectStateWriter(db, stateService, statesUpdate);
    var eventsWriter = new EventsWriter(db, new EventsService(db, map));
    var writer = new DeviceEventWriter(
      db,
      stateWriter,
      eventsWriter,
      new DeviceEventTranslator(),
      sensorAlarms,
      NullLogger<DeviceEventWriter>.Instance);

    var reconciler = new DeviceEventReconciler(
      db, nodes, stateWriter, eventsWriter, new DeviceEventTranslator(), sensorAlarms, faults,
      NullLogger<DeviceEventReconciler>.Instance);

    var objectId = await nodes.EnsureAsync(
      DeviceObjectId.CameraSource, CameraId, DeviceObjectId.DigitalInputPart, Token, default);

    if (settings is not null)
    {
      db.DeviceObjectSettings.Add(settings);
      await db.SaveChangesAsync();
    }

    return new Services(db, nodes, writer, reconciler, alarms, objectId!);
  }

  private static DeviceEventMessage Input(
    string contact, string epoch, long seq, DateTimeOffset? at = null) =>
    new(CameraSourceKind, CameraId, DeviceObjectId.DigitalInputPart, Token,
        System.Text.Json.JsonSerializer.SerializeToElement(contact),
        Active: null, Previous: null, Initialized: false,
        ObservedAt: at ?? DateTimeOffset.UtcNow,
        Epoch: epoch, Seq: seq, PolarityKnown: true);

  private static async Task<HashSet<string>> States(VmsMapDbContext db, string objectId)
  {
    var row = await db.ObjectStates.Include(s => s.states)
      .FirstOrDefaultAsync(s => s.id == Guid.Parse(objectId));
    return row is null
      ? []
      : row.states.Select(s => s.state).ToHashSet(StringComparer.Ordinal);
  }

  [Fact]
  public async Task AnOlderSequenceNumberIsDropped()
  {
    var s = await CreateAsync();
    var epoch = Guid.NewGuid().ToString();

    await s.Writer.ApplyAsync(s.ObjectId, Input(OpenContact, epoch, 5), default);
    await s.Writer.ApplyAsync(s.ObjectId, Input(ClosedContact, epoch, 4), default);

    var states = await States(s.Db, s.ObjectId);
    Assert.Contains(DeviceEventTranslator.SensorOpen, states);
    Assert.DoesNotContain(DeviceEventTranslator.SensorClosed, states);
    Assert.Equal(1, await s.Db.Events.CountAsync());
  }

  [Fact]
  public async Task ANewEpochIsAcceptedAndResetsTheCursor()
  {
    var s = await CreateAsync();
    var first = Guid.NewGuid().ToString();
    var second = Guid.NewGuid().ToString();
    var now = DateTimeOffset.UtcNow;

    await s.Writer.ApplyAsync(s.ObjectId, Input(OpenContact, first, 10, now), default);
    await s.Writer.ApplyAsync(s.ObjectId, Input(ClosedContact, second, 1, now.AddSeconds(1)), default);

    var states = await States(s.Db, s.ObjectId);
    Assert.Contains(DeviceEventTranslator.SensorClosed, states);
    Assert.DoesNotContain(DeviceEventTranslator.SensorOpen, states);
    var node = await s.Db.DeviceNodes.SingleAsync(n => n.object_id == Guid.Parse(s.ObjectId));
    Assert.Equal(Guid.Parse(second), node.last_epoch);
    Assert.Equal(1, node.last_seq);
  }

  /// The first thing a restarted publisher says is a snapshot, and it carries no sequence number.
  /// Keeping the dead process's counter would reject every transition the new one sends — it starts
  /// again at 1 — until the counter caught up. That is not a redelivery: it is the new process.
  [Fact]
  public async Task ASnapshotFromANewEpochLetsTheNewProcessStartItsSequenceOver()
  {
    var s = await CreateAsync();
    var first = Guid.NewGuid().ToString();
    var second = Guid.NewGuid().ToString();
    var now = DateTimeOffset.UtcNow;

    await s.Writer.ApplyAsync(s.ObjectId, Input(OpenContact, first, 42, now), default);

    await s.Reconciler.ApplyAsync(
      new DeviceSnapshotMessage(
        CameraSourceKind, CameraId, Reachable: true, now.AddSeconds(1), second, Device: null,
        [new DeviceSnapshotPartMessage(
          DeviceObjectId.DigitalInputPart, Token, IdleState: null,
          System.Text.Json.JsonSerializer.SerializeToElement(ClosedContact), Active: null,
          Present: true)]),
      default);

    var node = await s.Db.DeviceNodes.SingleAsync(n => n.object_id == Guid.Parse(s.ObjectId));
    Assert.Equal(Guid.Parse(second), node.last_epoch);
    Assert.Equal(0, node.last_seq);

    await s.Writer.ApplyAsync(s.ObjectId, Input(OpenContact, second, 1, now.AddSeconds(2)), default);

    Assert.Contains(DeviceEventTranslator.SensorOpen, await States(s.Db, s.ObjectId));
  }

  private static DeviceSnapshotMessage Snapshot(
    DeviceInfoMessage? device, DateTimeOffset? at = null) =>
    new(CameraSourceKind, CameraId, Reachable: true, at ?? DateTimeOffset.UtcNow,
        Guid.NewGuid().ToString(), device, []);

  [Fact]
  public async Task ASnapshotStoresWhatTheDeviceSaysAboutItself()
  {
    var s = await CreateAsync();

    await s.Reconciler.ApplyAsync(
      Snapshot(new DeviceInfoMessage(ManufacturerName, ModelName, FirmwareVersion)), default);

    // Stored, not remembered from the message: a diagram template must still match after the
    // process that heard the camera has restarted.
    var info = await s.Db.DeviceInfos.SingleAsync();
    Assert.Equal(DeviceObjectId.CameraSource, info.source_kind);
    Assert.Equal(CameraId, info.source_id);
    Assert.Equal(ManufacturerName, info.manufacturer);
    Assert.Equal(ModelName, info.model);
    Assert.Equal(FirmwareVersion, info.firmware);
  }

  [Fact]
  public async Task ASweepThatCouldNotAskLeavesTheStoredModelAlone()
  {
    var s = await CreateAsync();

    await s.Reconciler.ApplyAsync(
      Snapshot(new DeviceInfoMessage(ManufacturerName, ModelName, FirmwareVersion)), default);

    // A timeout is "not asked", not "says nothing": the network dropping for a minute must not
    // lose the model the diagram was matched by.
    await s.Reconciler.ApplyAsync(Snapshot(device: null), default);

    var info = await s.Db.DeviceInfos.SingleAsync();
    Assert.Equal(ModelName, info.model);
  }

  [Fact]
  public async Task AnAnswerThatSaysNothingReplacesTheModel()
  {
    var s = await CreateAsync();

    await s.Reconciler.ApplyAsync(
      Snapshot(new DeviceInfoMessage(ManufacturerName, ModelName, FirmwareVersion)), default);
    await s.Reconciler.ApplyAsync(Snapshot(new DeviceInfoMessage(EmptyValue, EmptyValue, EmptyValue)), default);

    // The camera answered. That is the fact the stored row follows, even when the answer is empty.
    var info = await s.Db.DeviceInfos.SingleAsync();
    Assert.Equal(string.Empty, info.model);
  }

  [Fact]
  public async Task AnEarlierObservationFromAnotherEpochIsDropped()
  {
    var s = await CreateAsync();
    var now = DateTimeOffset.UtcNow;

    await s.Writer.ApplyAsync(s.ObjectId, Input(OpenContact, Guid.NewGuid().ToString(), 1, now), default);
    await s.Writer.ApplyAsync(
      s.ObjectId, Input(ClosedContact, Guid.NewGuid().ToString(), 1, now.AddSeconds(1)), default);

    // A process whose clock is behind must not overwrite the newer state.
    await s.Writer.ApplyAsync(
      s.ObjectId, Input(OpenContact, Guid.NewGuid().ToString(), 1, now.AddSeconds(-10)), default);

    var states = await States(s.Db, s.ObjectId);
    Assert.Contains(DeviceEventTranslator.SensorClosed, states);
    Assert.DoesNotContain(DeviceEventTranslator.SensorOpen, states);
  }

  [Fact]
  public async Task TheSameDeliveryDoesNotWriteASecondEvent()
  {
    var s = await CreateAsync();
    var message = Input(OpenContact, Guid.NewGuid().ToString(), 7);

    await s.Writer.ApplyAsync(s.ObjectId, message, default);
    await s.Writer.ApplyAsync(s.ObjectId, message, default);

    Assert.Equal(1, await s.Db.Events.CountAsync());
  }

  [Fact]
  public async Task ARepeatedValueAdvancesTheCursorWithoutAnEvent()
  {
    var s = await CreateAsync();
    var epoch = Guid.NewGuid().ToString();

    await s.Writer.ApplyAsync(s.ObjectId, Input(OpenContact, epoch, 1), default);
    await s.Writer.ApplyAsync(s.ObjectId, Input(OpenContact, epoch, 2), default);

    Assert.Equal(1, await s.Db.Events.CountAsync());
    Assert.Equal(2, (await s.Db.DeviceNodes.SingleAsync(n => n.object_id == Guid.Parse(s.ObjectId))).last_seq);
  }

  // ── A part that vanishes from the manifest ───────────────────────────────────────────────────

  private static DBDeviceObjectSettings AlarmOn(string triggerOn) => new()
  {
    object_id = Guid.Parse(DeviceObjectId.For(
      DeviceObjectId.CameraSource, CameraId, DeviceObjectId.DigitalInputPart, Token)),
    arm_mode = ArmMode.Inherit,
    trigger_on = triggerOn,
    alarm_enabled = true,
  };

  private static DeviceSnapshotPartMessage Part(string contact, bool present = true) =>
    new(DeviceObjectId.DigitalInputPart, Token, IdleState: null,
        System.Text.Json.JsonSerializer.SerializeToElement(contact), Active: null, present);

  private static DeviceSnapshotMessage SnapshotOf(
    DateTimeOffset at, DeviceSnapshotPartMessage[] parts,
    string? epoch = null, bool reachable = true) =>
    new(CameraSourceKind, CameraId, reachable, at,
        epoch ?? Guid.NewGuid().ToString(), Device: null, parts);

  private static Task<DBAlarm?> OpenAlarm(VmsMapDbContext db) =>
    db.Alarms.AsNoTracking()
      .Where(a => a.status != AlarmStatus.Closed)
      .OrderByDescending(a => a.raised_at)
      .FirstOrDefaultAsync();

  /// <summary>The open alarm of one source: the condition alarm and the fault are separate rows.</summary>
  private static Task<DBAlarm?> AlarmBySource(VmsMapDbContext db, string source) =>
    db.Alarms.AsNoTracking()
      .Where(a => a.source == source && a.status != AlarmStatus.Closed)
      .OrderByDescending(a => a.raised_at)
      .FirstOrDefaultAsync();

  private static async Task<bool> PresentAsync(VmsMapDbContext db, string objectId) =>
    (await db.DeviceNodes.AsNoTracking()
      .SingleAsync(n => n.object_id == Guid.Parse(objectId))).present;

  // The manifest is the only statement allowed to say a part is gone. A part it no longer lists
  // stops reading as live, but its alarm is not the system's to close: the condition cannot clear
  // by itself, and an operator resolves that fault with a comment.
  [Fact]
  public async Task APartMissingFromTheManifestLosesItsStateButKeepsItsAlarm()
  {
    var s = await CreateAsync(armed: true, settings: AlarmOn(ClosedContact));
    var at = DateTimeOffset.UtcNow;

    await s.Reconciler.ApplyAsync(SnapshotOf(at, [Part(ClosedContact)]), default);

    Assert.NotNull(await OpenAlarm(s.Db));
    Assert.Contains(DeviceEventTranslator.SensorClosed, await States(s.Db, s.ObjectId));

    await s.Reconciler.ApplyAsync(SnapshotOf(at.AddMinutes(1), []), default);

    var states = await States(s.Db, s.ObjectId);
    Assert.DoesNotContain(DeviceEventTranslator.SensorClosed, states);
    Assert.DoesNotContain(DeviceEventTranslator.SensorOpen, states);
    Assert.Contains(CameraStateWriter.Alarm, states);
    Assert.Equal(AlarmStatus.Active, (await AlarmBySource(s.Db, AlarmSource.Sensor))!.status);

    var fault = await AlarmBySource(s.Db, AlarmSource.Fault);
    Assert.NotNull(fault);
    Assert.Equal(AlarmStatus.Active, fault!.status);
    Assert.True(fault.condition_active);
    Assert.False(await PresentAsync(s.Db, s.ObjectId));
  }

  [Fact]
  public async Task RepeatedMissesDoNotCloseTheAlarmByThemselves()
  {
    var s = await CreateAsync(armed: true, settings: AlarmOn(ClosedContact));
    var at = DateTimeOffset.UtcNow;

    await s.Reconciler.ApplyAsync(SnapshotOf(at, [Part(ClosedContact)]), default);

    for (var minute = 1; minute <= 3; minute++)
      await s.Reconciler.ApplyAsync(SnapshotOf(at.AddMinutes(minute), []), default);

    Assert.Equal(AlarmStatus.Active, (await AlarmBySource(s.Db, AlarmSource.Sensor))!.status);
    Assert.Single(await s.Db.Alarms
      .Where(a => a.source == AlarmSource.Sensor && a.status != AlarmStatus.Closed)
      .ToListAsync());
    Assert.Single(await s.Db.Alarms
      .Where(a => a.source == AlarmSource.Fault && a.status != AlarmStatus.Closed)
      .ToListAsync());
  }

  [Fact]
  public async Task APartThatComesBackRestoresItsStateWithoutASecondAlarm()
  {
    var s = await CreateAsync(armed: true, settings: AlarmOn(ClosedContact));
    var at = DateTimeOffset.UtcNow;

    await s.Reconciler.ApplyAsync(SnapshotOf(at, [Part(ClosedContact)]), default);
    await s.Reconciler.ApplyAsync(SnapshotOf(at.AddMinutes(1), []), default);
    await s.Reconciler.ApplyAsync(SnapshotOf(at.AddMinutes(2), [Part(ClosedContact)]), default);

    Assert.Contains(DeviceEventTranslator.SensorClosed, await States(s.Db, s.ObjectId));
    Assert.True(await PresentAsync(s.Db, s.ObjectId));
    Assert.Single(await s.Db.Alarms
      .Where(a => a.source == AlarmSource.Sensor && a.status != AlarmStatus.Closed)
      .ToListAsync());

    var fault = await AlarmBySource(s.Db, AlarmSource.Fault);
    Assert.NotNull(fault);
    Assert.Equal(AlarmStatus.Returned, fault!.status);
    Assert.False(fault.condition_active);
  }

  [Fact]
  public async Task APartClosedByTheOperatorRaisesAgainWhenItComesBack()
  {
    var s = await CreateAsync(armed: true, settings: AlarmOn(ClosedContact));
    var at = DateTimeOffset.UtcNow;

    await s.Reconciler.ApplyAsync(SnapshotOf(at, [Part(ClosedContact)]), default);
    await s.Reconciler.ApplyAsync(SnapshotOf(at.AddMinutes(1), []), default);

    var alarm = (await AlarmBySource(s.Db, AlarmSource.Sensor))!;
    var outcome = await s.Alarms.CloseAsync(
      alarm.id, "alice", AlarmCloseReason.Other, "the sensor was removed", default);
    Assert.Equal(AlarmMove.Applied, outcome.Move);
    Assert.Null(await AlarmBySource(s.Db, AlarmSource.Sensor));

    await s.Reconciler.ApplyAsync(SnapshotOf(at.AddMinutes(2), [Part(ClosedContact)]), default);

    Assert.NotNull(await AlarmBySource(s.Db, AlarmSource.Sensor));
    Assert.Equal(AlarmStatus.Returned, (await AlarmBySource(s.Db, AlarmSource.Fault))!.status);
    Assert.Contains(DeviceEventTranslator.SensorClosed, await States(s.Db, s.ObjectId));
  }

  [Fact]
  public async Task APartMissingWithoutAConditionAlarmStillRaisesAFault()
  {
    var s = await CreateAsync();
    var at = DateTimeOffset.UtcNow;

    await s.Reconciler.ApplyAsync(SnapshotOf(at, [Part(ClosedContact)]), default);
    await s.Reconciler.ApplyAsync(SnapshotOf(at.AddMinutes(1), []), default);

    var fault = await AlarmBySource(s.Db, AlarmSource.Fault);
    Assert.NotNull(fault);
    Assert.Equal(AlarmStatus.Active, fault!.status);
    Assert.True(fault.condition_active);
    Assert.Null(await AlarmBySource(s.Db, AlarmSource.Sensor));
    Assert.False(await PresentAsync(s.Db, s.ObjectId));
    Assert.DoesNotContain(CameraStateWriter.Alarm, await States(s.Db, s.ObjectId));
  }

  [Fact]
  public async Task ARelayMissingFromTheManifestRaisesAFault()
  {
    var s = await CreateAsync();
    var at = DateTimeOffset.UtcNow;
    var relay = Guid.Parse(DeviceObjectId.For(
      CameraSourceKind, CameraId, DeviceObjectId.RelayPart, "relay1"));

    await s.Reconciler.ApplyAsync(SnapshotOf(at,
      [new DeviceSnapshotPartMessage(
        DeviceObjectId.RelayPart, "relay1", IdleState: null, Contact: null,
        Active: System.Text.Json.JsonSerializer.SerializeToElement(true), Present: true)]),
      default);
    await s.Reconciler.ApplyAsync(SnapshotOf(at.AddMinutes(1), []), default);

    var fault = await AlarmBySource(s.Db, AlarmSource.Fault);
    Assert.NotNull(fault);
    Assert.Equal(relay, fault!.object_id);
    Assert.Equal(AlarmStatus.Active, fault.status);
  }

  [Fact]
  public async Task AConfiguredPartMissingFromTheFirstManifestGetsAFault()
  {
    var s = await CreateAsync();
    var configured = await s.Nodes.EnsureAsync(
      CameraSourceKind, CameraId, DeviceObjectId.DigitalInputPart, "configured", default);
    Assert.NotNull(configured);

    await s.Reconciler.ApplyAsync(
      SnapshotOf(DateTimeOffset.UtcNow, [Part(ClosedContact)]), default);

    var node = await s.Db.DeviceNodes.AsNoTracking()
      .SingleAsync(n => n.object_id == Guid.Parse(configured!));
    Assert.False(node.present);

    var fault = await AlarmBySource(s.Db, AlarmSource.Fault);
    Assert.NotNull(fault);
    Assert.Equal(Guid.Parse(configured!), fault!.object_id);
  }

  [Fact]
  public async Task AFaultIsReturnedWhenThePartComesBackWithoutAConditionAlarm()
  {
    var s = await CreateAsync();
    var at = DateTimeOffset.UtcNow;

    await s.Reconciler.ApplyAsync(SnapshotOf(at, [Part(ClosedContact)]), default);
    await s.Reconciler.ApplyAsync(SnapshotOf(at.AddMinutes(1), []), default);
    var fault = await AlarmBySource(s.Db, AlarmSource.Fault);
    Assert.NotNull(fault);

    await s.Reconciler.ApplyAsync(SnapshotOf(at.AddMinutes(2), [Part(ClosedContact)]), default);

    var row = await AlarmBySource(s.Db, AlarmSource.Fault);
    Assert.NotNull(row);
    Assert.Equal(fault!.id, row!.id);
    Assert.Equal(AlarmStatus.Returned, row.status);
    Assert.False(row.condition_active);
    Assert.True(await PresentAsync(s.Db, s.ObjectId));
    Assert.DoesNotContain(CameraStateWriter.Alarm, await States(s.Db, s.ObjectId));
  }

  [Fact]
  public async Task AFaultReactivatesTheSameRowWhenThePartGoesAgain()
  {
    var s = await CreateAsync();
    var at = DateTimeOffset.UtcNow;

    await s.Reconciler.ApplyAsync(SnapshotOf(at, [Part(ClosedContact)]), default);
    await s.Reconciler.ApplyAsync(SnapshotOf(at.AddMinutes(1), []), default);
    var first = (await AlarmBySource(s.Db, AlarmSource.Fault))!;

    await s.Reconciler.ApplyAsync(SnapshotOf(at.AddMinutes(2), [Part(ClosedContact)]), default);
    await s.Reconciler.ApplyAsync(SnapshotOf(at.AddMinutes(3), []), default);

    var again = await AlarmBySource(s.Db, AlarmSource.Fault);
    Assert.NotNull(again);
    Assert.Equal(first.id, again!.id);
    Assert.Equal(AlarmStatus.Active, again.status);
    Assert.Equal(2, await s.Db.Events.CountAsync(e => e.event_name == AlarmStore.RaisedEvent));
  }

  [Fact]
  public async Task AFaultDoesNotLightTheNodeOrItsParents()
  {
    var s = await CreateAsync();
    var at = DateTimeOffset.UtcNow;

    await s.Reconciler.ApplyAsync(SnapshotOf(at, [Part(ClosedContact)]), default);
    await s.Reconciler.ApplyAsync(SnapshotOf(at.AddMinutes(1), []), default);

    Assert.DoesNotContain(CameraStateWriter.Alarm, await States(s.Db, s.ObjectId));

    var nodeAlarm = await s.Db.AlarmStates.AsNoTracking()
      .FirstOrDefaultAsync(a => a.id == Guid.Parse(s.ObjectId));
    Assert.True(nodeAlarm is null || !nodeAlarm.alarm);

    var cameraAlarm = await s.Db.AlarmStates.AsNoTracking()
      .FirstOrDefaultAsync(a => a.id == Guid.Parse(CameraObjectId.For(CameraId)));
    Assert.True(cameraAlarm is null || cameraAlarm.children_alarms == 0);
  }

  [Fact]
  public async Task AnUnansweredSweepDoesNotMarkAPartAbsent()
  {
    var s = await CreateAsync(armed: true, settings: AlarmOn(ClosedContact));
    var at = DateTimeOffset.UtcNow;

    await s.Reconciler.ApplyAsync(SnapshotOf(at, [Part(ClosedContact)]), default);
    await s.Reconciler.ApplyAsync(
      SnapshotOf(at.AddMinutes(1), [], reachable: false), default);

    Assert.True(await PresentAsync(s.Db, s.ObjectId));
    Assert.Contains(DeviceEventTranslator.SensorClosed, await States(s.Db, s.ObjectId));
    Assert.NotNull(await OpenAlarm(s.Db));
  }

  [Fact]
  public async Task ALateSnapshotCannotMarkAPartAbsent()
  {
    var s = await CreateAsync(armed: true, settings: AlarmOn(ClosedContact));
    var at = DateTimeOffset.UtcNow;

    await s.Reconciler.ApplyAsync(
      SnapshotOf(at, [Part(ClosedContact)], epoch: Guid.NewGuid().ToString()), default);

    // A process whose answer was observed five minutes ago, and which no longer lists the part.
    await s.Reconciler.ApplyAsync(
      SnapshotOf(at.AddMinutes(-5), [], epoch: Guid.NewGuid().ToString()), default);

    Assert.True(await PresentAsync(s.Db, s.ObjectId));
    Assert.Contains(DeviceEventTranslator.SensorClosed, await States(s.Db, s.ObjectId));
  }

  // The camera's own row is not part of the manifest a sweep restates: the camera answered, so it
  // is present. A retention alarm on its node must survive every snapshot.
  [Fact]
  public async Task ACameraSnapshotNeverMarksTheCameraAbsent()
  {
    var s = await CreateAsync();
    var camera = Guid.Parse(CameraObjectId.For(CameraId));

    await s.Alarms.RaiseAsync(new RaiseAlarmSpec(
      camera, AlarmSource.Retention, "retention:1"), default);

    await s.Reconciler.ApplyAsync(SnapshotOf(DateTimeOffset.UtcNow, []), default);

    Assert.True(await PresentAsync(s.Db, camera.ToString()));
    Assert.NotNull(await OpenAlarm(s.Db));
  }
}
