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

namespace VmsMap.Tests;

// What a device says, and what the map makes of it: the state dictionary, the journal event with
// its addressing, and the (epoch, seq) cursor that keeps a redelivery from turning the contact back.
public class DeviceEventTranslatorTests
{
  private const string CameraId = "3f2504e0-4f89-11d3-9a0c-0305e82c3301";

  private static DeviceEventMessage Message(
    string partKind, JsonElement? contact = null, JsonElement? active = null) =>
    new("camera", CameraId, partKind, "part1", contact, active,
        Previous: null, Initialized: false, DateTimeOffset.UtcNow,
        Epoch: Guid.NewGuid().ToString(), Seq: 1, PolarityKnown: true);

  private static JsonElement Text(string value) => JsonSerializer.SerializeToElement(value);
  private static JsonElement Bool(bool value) => JsonSerializer.SerializeToElement(value);

  [Theory]
  [InlineData("open", DeviceEventTranslator.SensorOpen)]
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
      Message(DeviceObjectId.DigitalInputPart, contact: Text("open")));

    var props = change!.ExtraProps.ToDictionary(p => p.prop_name!, p => p.str_val);
    Assert.Equal(CameraId, props["camera_id"]);
    Assert.Equal(DeviceObjectId.DigitalInputPart, props["part_kind"]);
    Assert.Equal("part1", props["token"]);
    Assert.Equal("open", props["contact"]);
    Assert.Equal("true", props["polarity_known"]);
    Assert.True(props.ContainsKey("observed_at"));
  }
}

public class DeviceEventWriterTests
{
  private const string CameraId = "3f2504e0-4f89-11d3-9a0c-0305e82c3301";
  private const string Token = "input1";

  private sealed record Services(
    VmsMapDbContext Db, DeviceNodeService Nodes, DeviceEventWriter Writer,
    DeviceEventReconciler Reconciler, string ObjectId);

  private static async Task<Services> CreateAsync()
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
    var updates = new MapUpdateService(map, tracks, statesUpdate);
    var nodes = TestWiring.DeviceNodes(db, map, updates);

    var sensorAlarms = TestWiring.SensorAlarms(db, statesUpdate, map);
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
      db, nodes, stateWriter, eventsWriter, new DeviceEventTranslator(), sensorAlarms,
      TestWiring.Alarms(db, map, statesUpdate),
      NullLogger<DeviceEventReconciler>.Instance);

    var objectId = await nodes.EnsureAsync(
      DeviceObjectId.CameraSource, CameraId, DeviceObjectId.DigitalInputPart, Token, default);

    return new Services(db, nodes, writer, reconciler, objectId!);
  }

  private static DeviceEventMessage Input(
    string contact, string epoch, long seq, DateTimeOffset? at = null) =>
    new("camera", CameraId, DeviceObjectId.DigitalInputPart, Token,
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

    await s.Writer.ApplyAsync(s.ObjectId, Input("open", epoch, 5), default);
    await s.Writer.ApplyAsync(s.ObjectId, Input("closed", epoch, 4), default);

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

    await s.Writer.ApplyAsync(s.ObjectId, Input("open", first, 10, now), default);
    await s.Writer.ApplyAsync(s.ObjectId, Input("closed", second, 1, now.AddSeconds(1)), default);

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

    await s.Writer.ApplyAsync(s.ObjectId, Input("open", first, 42, now), default);

    await s.Reconciler.ApplyAsync(
      new DeviceSnapshotMessage(
        "camera", CameraId, Reachable: true, now.AddSeconds(1), second, Device: null,
        [new DeviceSnapshotPartMessage(
          DeviceObjectId.DigitalInputPart, Token, IdleState: null,
          System.Text.Json.JsonSerializer.SerializeToElement("closed"), Active: null,
          Present: true)]),
      default);

    var node = await s.Db.DeviceNodes.SingleAsync(n => n.object_id == Guid.Parse(s.ObjectId));
    Assert.Equal(Guid.Parse(second), node.last_epoch);
    Assert.Equal(0, node.last_seq);

    await s.Writer.ApplyAsync(s.ObjectId, Input("open", second, 1, now.AddSeconds(2)), default);

    Assert.Contains(DeviceEventTranslator.SensorOpen, await States(s.Db, s.ObjectId));
  }

  private static DeviceSnapshotMessage Snapshot(
    DeviceInfoMessage? device, DateTimeOffset? at = null) =>
    new("camera", CameraId, Reachable: true, at ?? DateTimeOffset.UtcNow,
        Guid.NewGuid().ToString(), device, []);

  [Fact]
  public async Task ASnapshotStoresWhatTheDeviceSaysAboutItself()
  {
    var s = await CreateAsync();

    await s.Reconciler.ApplyAsync(
      Snapshot(new DeviceInfoMessage("OnvifSim", "Simulated fixed camera", "0.1.0")), default);

    // Stored, not remembered from the message: a diagram template must still match after the
    // process that heard the camera has restarted.
    var info = await s.Db.DeviceInfos.SingleAsync();
    Assert.Equal(DeviceObjectId.CameraSource, info.source_kind);
    Assert.Equal(CameraId, info.source_id);
    Assert.Equal("OnvifSim", info.manufacturer);
    Assert.Equal("Simulated fixed camera", info.model);
    Assert.Equal("0.1.0", info.firmware);
  }

  [Fact]
  public async Task ASweepThatCouldNotAskLeavesTheStoredModelAlone()
  {
    var s = await CreateAsync();

    await s.Reconciler.ApplyAsync(
      Snapshot(new DeviceInfoMessage("OnvifSim", "Simulated fixed camera", "0.1.0")), default);

    // A timeout is "not asked", not "says nothing": the network dropping for a minute must not
    // lose the model the diagram was matched by.
    await s.Reconciler.ApplyAsync(Snapshot(device: null), default);

    var info = await s.Db.DeviceInfos.SingleAsync();
    Assert.Equal("Simulated fixed camera", info.model);
  }

  [Fact]
  public async Task AnAnswerThatSaysNothingReplacesTheModel()
  {
    var s = await CreateAsync();

    await s.Reconciler.ApplyAsync(
      Snapshot(new DeviceInfoMessage("OnvifSim", "Simulated fixed camera", "0.1.0")), default);
    await s.Reconciler.ApplyAsync(Snapshot(new DeviceInfoMessage("", "", "")), default);

    // The camera answered. That is the fact the stored row follows, even when the answer is empty.
    var info = await s.Db.DeviceInfos.SingleAsync();
    Assert.Equal(string.Empty, info.model);
  }

  [Fact]
  public async Task AnEarlierObservationFromAnotherEpochIsDropped()
  {
    var s = await CreateAsync();
    var now = DateTimeOffset.UtcNow;

    await s.Writer.ApplyAsync(s.ObjectId, Input("open", Guid.NewGuid().ToString(), 1, now), default);
    await s.Writer.ApplyAsync(
      s.ObjectId, Input("closed", Guid.NewGuid().ToString(), 1, now.AddSeconds(1)), default);

    // A process whose clock is behind must not overwrite the newer state.
    await s.Writer.ApplyAsync(
      s.ObjectId, Input("open", Guid.NewGuid().ToString(), 1, now.AddSeconds(-10)), default);

    var states = await States(s.Db, s.ObjectId);
    Assert.Contains(DeviceEventTranslator.SensorClosed, states);
    Assert.DoesNotContain(DeviceEventTranslator.SensorOpen, states);
  }

  [Fact]
  public async Task TheSameDeliveryDoesNotWriteASecondEvent()
  {
    var s = await CreateAsync();
    var message = Input("open", Guid.NewGuid().ToString(), 7);

    await s.Writer.ApplyAsync(s.ObjectId, message, default);
    await s.Writer.ApplyAsync(s.ObjectId, message, default);

    Assert.Equal(1, await s.Db.Events.CountAsync());
  }

  [Fact]
  public async Task ARepeatedValueAdvancesTheCursorWithoutAnEvent()
  {
    var s = await CreateAsync();
    var epoch = Guid.NewGuid().ToString();

    await s.Writer.ApplyAsync(s.ObjectId, Input("open", epoch, 1), default);
    await s.Writer.ApplyAsync(s.ObjectId, Input("open", epoch, 2), default);

    Assert.Equal(1, await s.Db.Events.CountAsync());
    Assert.Equal(2, (await s.Db.DeviceNodes.SingleAsync(n => n.object_id == Guid.Parse(s.ObjectId))).last_seq);
  }
}
