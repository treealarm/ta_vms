using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using VmsDb;
using VmsMap.Adapter;
using VmsMap.Db;
using VmsMap.Domain;

namespace VmsMap.Tests;

// What only PostgreSQL can prove about the alarm entity: the transaction around a transition, the
// partial unique index that decides which of two concurrent raises created the row, and the
// aggregation ALARM drives up the tree. Enabled by VMS_MAP_TEST_DSN, like PostgresTests.
[Collection("postgis")]
public class AlarmStorePostgresTests
{
  // The fixtures these tests share. The node names are labels no test asserts; the token is the
  // digital input's part address and the sensor keys are the store's dedup addresses.
  private const string AliceName = "alice";
  private const string ResetComment = "the part reports again";
  private const string InputToken = "1";
  private const string BuildingName = "building";
  private const string FloorName = "floor";
  private const string CameraNodeName = "camera";
  private const string DoorNodeName = "door";
  private const string SensorOneKey = "sensor:1";
  private const string SensorTwoKey = "sensor:two";

  private static readonly string? Dsn =
    Environment.GetEnvironmentVariable("VMS_MAP_TEST_DSN");

  private sealed record Services(
    NpgsqlDataSource DataSource,
    VmsMapDbContext Db,
    MapDb Map,
    StatesUpdateService States,
    MapUpdateService Updates,
    AlarmStore Alarms,
    TestCameraState Arming) : IAsyncDisposable
  {
    public async ValueTask DisposeAsync()
    {
      await Db.DisposeAsync();
      await DataSource.DisposeAsync();
    }
  }

  private static AlarmStore Store(VmsMapDbContext db, MapDb map)
  {
    var stateService = new StateService(db);
    var states = new StatesUpdateService(stateService, map, db);
    return new AlarmStore(
      db, new ObjectStateWriter(db, stateService, states),
      new EventsWriter(db, new EventsService(db, map)),
      new TestAlarmDefinitions(), TestWiring.Arming(db),
      NullLogger<AlarmStore>.Instance);
  }

  private static async Task<Services?> OpenAsync()
  {
    if (string.IsNullOrWhiteSpace(Dsn))
      return null;

    await using (var migrationSource = new NpgsqlDataSourceBuilder(Dsn).Build())
    {
      await new VmsDbMigrator(migrationSource, "VmsMapTest", AppContext.BaseDirectory, "7743512881")
        .MigrateAsync();
    }

    var dataSourceBuilder = new NpgsqlDataSourceBuilder(Dsn);
    dataSourceBuilder.EnableDynamicJson();
    dataSourceBuilder.UseNetTopologySuite();
    var dataSource = dataSourceBuilder.Build();

    var options = new DbContextOptionsBuilder<VmsMapDbContext>()
      .UseNpgsql(dataSource, o => o.UseNetTopologySuite())
      .Options;

    var db = new VmsMapDbContext(options);
    await db.Database.ExecuteSqlRawAsync("TRUNCATE objects CASCADE;");
    // The outbox has no foreign key to the tree, so the cascade does not reach it.
    await db.Database.ExecuteSqlRawAsync("TRUNCATE map_outbox;");

    var map = new MapDb(db);
    var stateService = new StateService(db);
    var states = new StatesUpdateService(stateService, map, db);
    var levels = new LevelService(db);
    var tracks = new TracksUpdateService(map, new TrackService(db, levels), new GeoService(db, levels));
    var updates = new MapUpdateService(db, map, tracks, states);
    var arming = new TestCameraState();

    return new Services(
      dataSource, db, map, states, updates, Store(db, map), arming);
  }

  /// <summary>A second store on its own context and connection, as a second replica would be.</summary>
  private static AlarmStore SecondStore(Services s, out VmsMapDbContext db)
  {
    var options = new DbContextOptionsBuilder<VmsMapDbContext>()
      .UseNpgsql(s.DataSource, o => o.UseNetTopologySuite())
      .Options;
    db = new VmsMapDbContext(options);
    return Store(db, new MapDb(db));
  }

  private sealed record Tree(Guid Root, Guid Floor, Guid Camera, Guid Input);

  private static async Task<Tree> SeedAsync(Services s)
  {
    var root = Guid.NewGuid();
    var floor = Guid.NewGuid();
    var camera = Guid.Parse(CameraObjectId.For(TestCameras.One));
    var input = Guid.Parse(DeviceObjectId.For(
      DeviceObjectId.CameraSource, TestCameras.One, DeviceObjectId.DigitalInputPart, InputToken));

    s.Db.Markers.AddRange(
      new DBMarker { id = root, name = BuildingName },
      new DBMarker { id = floor, name = FloorName, parent_id = root },
      new DBMarker { id = camera, name = CameraNodeName, parent_id = floor },
      new DBMarker { id = input, name = DoorNodeName, parent_id = camera });
    await s.Db.SaveChangesAsync();

    // A second save: the model has no navigation from device_node to objects, so EF cannot order
    // the two inserts itself.
    s.Db.DeviceNodes.AddRange(
      TestCameras.DeviceNode(TestCameras.One, camera),
      new DBDeviceNode
      {
        object_id = input,
        source_kind = DeviceObjectId.CameraSource,
        source_id = TestCameras.One,
        part_kind = DeviceObjectId.DigitalInputPart,
        part_token = InputToken,
      });
    await s.Db.SaveChangesAsync();

    return new Tree(root, floor, camera, input);
  }

  private static Task<DBAlarmState?> AlarmAsync(VmsMapDbContext db, Guid id) =>
    db.AlarmStates.AsNoTracking().FirstOrDefaultAsync(a => a.id == id);

  private static RaiseAlarmSpec Sensor(Guid input, string key = SensorOneKey) =>
    new(input, AlarmSource.Sensor, key, Trigger: "open");

  private static RaiseAlarmSpec Fault(Guid input) =>
    new(input, AlarmSource.Fault, DeviceFaultService.DedupKey(input),
        Severity: AlarmSeverity.Warning, Trigger: DeviceFaultService.PartAbsentTrigger);

  [Fact]
  public async Task ARaiseWritesTheRowAndTheStateInOneTransaction()
  {
    await using var s = await OpenAsync();
    if (s is null) return;

    var tree = await SeedAsync(s);

    await s.Alarms.RaiseAsync(Sensor(tree.Input), default);

    var row = await s.Db.Alarms.AsNoTracking().SingleAsync();
    Assert.Equal(AlarmStatus.Active, row.status);
    Assert.True(row.condition_active);
    Assert.NotNull(row.definition_id);

    // ALARM on the part, and its counter all the way up: camera, floor, building.
    Assert.True((await AlarmAsync(s.Db, tree.Input))!.alarm);
    Assert.Equal(1, (await AlarmAsync(s.Db, tree.Camera))!.children_alarms);
    Assert.Equal(1, (await AlarmAsync(s.Db, tree.Floor))!.children_alarms);
    Assert.Equal(1, (await AlarmAsync(s.Db, tree.Root))!.children_alarms);

    // The event and its outbox row are there, so the notification cannot have been lost.
    Assert.Single(await s.Db.Events.AsNoTracking().ToListAsync());
    Assert.Single(await s.Db.MapOutbox.AsNoTracking().ToListAsync());
  }

  [Fact]
  public async Task TwoReplicasRaisingTheSameConditionMakeOneRow()
  {
    await using var s = await OpenAsync();
    if (s is null) return;

    var tree = await SeedAsync(s);
    var second = SecondStore(s, out var otherDb);
    await using var _ = otherDb;

    var results = await Task.WhenAll(
      s.Alarms.RaiseAsync(Sensor(tree.Input), default),
      second.RaiseAsync(Sensor(tree.Input), default));

    Assert.Single(await s.Db.Alarms.AsNoTracking().ToListAsync());
    Assert.Equal(1, await s.Db.Events.CountAsync(e => e.event_name == AlarmStore.RaisedEvent));
    Assert.Contains(results, r => r.Move == AlarmMove.Applied);
    Assert.True((await AlarmAsync(s.Db, tree.Input))!.alarm);
  }

  [Fact]
  public async Task AnAckRacingARaiseKeepsTheAlarmRowAndItsState()
  {
    await using var s = await OpenAsync();
    if (s is null) return;

    var tree = await SeedAsync(s);
    s.Arming.Armed = false;

    var raised = await s.Alarms.RaiseAsync(Sensor(tree.Input), default);
    var second = SecondStore(s, out var otherDb);
    await using var _ = otherDb;

    await Task.WhenAll(
      s.Alarms.AcknowledgeAsync(raised.Alarm!.id, AliceName, null, default),
      second.RaiseAsync(Sensor(tree.Input), default));

    var row = await s.Db.Alarms.AsNoTracking().SingleAsync();
    Assert.Equal(AlarmStatus.Acknowledged, row.status);
    Assert.True(row.condition_active);
    Assert.True((await AlarmAsync(s.Db, tree.Input))!.alarm);
  }

  [Fact]
  public async Task ASubscriptionRaiseIsIdempotentEvenAfterTheAlarmWasClosed()
  {
    await using var s = await OpenAsync();
    if (s is null) return;

    var tree = await SeedAsync(s);

    // The router's operation: one subscription action, one trigger event. A redelivery after the
    // next_position write was lost computes exactly the same three values.
    var spec = new RaiseAlarmSpec(
      tree.Input,
      AlarmSource.Subscription,
      "subscription:subject",
      SourceEventId: Guid.NewGuid(),
      SubscriptionId: Guid.NewGuid());

    var first = await s.Alarms.RaiseAsync(spec, default);
    Assert.Equal(AlarmMove.Applied, first.Move);

    // The condition clears and an operator closes the alarm before the repeat arrives.
    await s.Alarms.NormalAsync(tree.Input, spec.DedupKey, default);
    await s.Alarms.CloseAsync(
      first.Alarm!.id, AliceName, AlarmCloseReason.FalseAlarm, null, default);

    var repeat = await s.Alarms.RaiseAsync(spec, default);

    Assert.Equal(AlarmMove.Unchanged, repeat.Move);
    Assert.Equal(first.Alarm.id, repeat.Alarm!.id);
    Assert.Single(await s.Db.Alarms.AsNoTracking().ToListAsync());
    Assert.Single(await s.Db.Events.AsNoTracking()
      .Where(e => e.event_name == AlarmStore.RaisedEvent).ToListAsync());
  }

  [Fact]
  public async Task ALIVEAlarmOnAnotherChildKeepsTheAncestorCounted()
  {
    await using var s = await OpenAsync();
    if (s is null) return;

    var tree = await SeedAsync(s);

    var otherCamera = Guid.Parse(CameraObjectId.For(TestCameras.Two));
    var otherInput = Guid.Parse(DeviceObjectId.For(
      DeviceObjectId.CameraSource, TestCameras.Two, DeviceObjectId.DigitalInputPart, InputToken));
    s.Db.Markers.AddRange(
      new DBMarker { id = otherCamera, name = "other camera", parent_id = tree.Floor },
      new DBMarker { id = otherInput, name = "other door", parent_id = otherCamera });
    await s.Db.SaveChangesAsync();

    s.Db.DeviceNodes.AddRange(
      TestCameras.DeviceNode(TestCameras.Two, otherCamera),
      new DBDeviceNode
      {
        object_id = otherInput,
        source_kind = DeviceObjectId.CameraSource,
        source_id = TestCameras.Two,
        part_kind = DeviceObjectId.DigitalInputPart,
        part_token = InputToken,
      });
    await s.Db.SaveChangesAsync();

    await s.Alarms.RaiseAsync(Sensor(tree.Input, "sensor:one"), default);
    await s.Alarms.RaiseAsync(Sensor(otherInput, SensorTwoKey), default);
    Assert.Equal(2, (await AlarmAsync(s.Db, tree.Floor))!.children_alarms);

    await s.Alarms.NormalAsync(otherInput, SensorTwoKey, default);

    Assert.Equal(1, (await AlarmAsync(s.Db, tree.Floor))!.children_alarms);
    Assert.Equal(1, (await AlarmAsync(s.Db, tree.Root))!.children_alarms);
    Assert.True((await AlarmAsync(s.Db, tree.Input))!.alarm);
  }

  [Fact]
  public async Task AcknowledgingDoesNotChangeAlarmStates()
  {
    await using var s = await OpenAsync();
    if (s is null) return;

    var tree = await SeedAsync(s);
    s.Arming.Armed = false;

    var raised = await s.Alarms.RaiseAsync(Sensor(tree.Input), default);
    var before = await s.Db.AlarmStates.AsNoTracking()
      .ToDictionaryAsync(a => a.id, a => (a.alarm, a.children_alarms));

    await s.Alarms.AcknowledgeAsync(raised.Alarm!.id, AliceName, null, default);

    var after = await s.Db.AlarmStates.AsNoTracking()
      .ToDictionaryAsync(a => a.id, a => (a.alarm, a.children_alarms));
    Assert.Equal(before, after);
    Assert.True((await AlarmAsync(s.Db, tree.Input))!.alarm);
  }

  // The room above a camera is not an ancestor of its canonical node: the canonical node hangs
  // under the server, and only the reference replica stands on the floor plan. The mirror is what
  // makes the room count the camera's alarm, and removing it again when the contact returns.
  private sealed record Room(Guid Building, Guid Floor, Guid Camera, Guid Input, Guid Replica);

  private static async Task<Room> SeedRoomAsync(Services s)
  {
    var building = Guid.NewGuid();
    var floor = Guid.NewGuid();
    var server = Guid.NewGuid();
    var camera = Guid.Parse(CameraObjectId.For(TestCameras.One));
    var input = Guid.Parse(DeviceObjectId.For(
      DeviceObjectId.CameraSource, TestCameras.One, DeviceObjectId.DigitalInputPart, InputToken));
    var replica = Guid.NewGuid();

    s.Db.Markers.AddRange(
      new DBMarker { id = building, name = BuildingName },
      new DBMarker { id = floor, name = FloorName, parent_id = building },
      new DBMarker { id = server, name = "server" },
      new DBMarker { id = camera, name = CameraNodeName, parent_id = server },
      new DBMarker { id = input, name = DoorNodeName, parent_id = camera },
      // A picture of the camera on the floor plan, where a person looks for it.
      new DBMarker { id = replica, name = CameraNodeName, parent_id = floor, owner_id = camera });
    await s.Db.SaveChangesAsync();

    s.Db.DeviceNodes.AddRange(
      TestCameras.DeviceNode(TestCameras.One, camera),
      new DBDeviceNode
      {
        object_id = input,
        source_kind = DeviceObjectId.CameraSource,
        source_id = TestCameras.One,
        part_kind = DeviceObjectId.DigitalInputPart,
        part_token = InputToken,
      });
    s.Db.CameraPlacements.Add(new DBCameraPlacement
    {
      object_id = replica,
      camera_id = TestCameras.One,
      is_reference = true,
    });
    await s.Db.SaveChangesAsync();

    return new Room(building, floor, camera, input, replica);
  }

  [Fact]
  public async Task TheRoomCountsTheCameraThatStandsInIt()
  {
    await using var s = await OpenAsync();
    if (s is null) return;

    var room = await SeedRoomAsync(s);

    await s.Alarms.RaiseAsync(Sensor(room.Input), default);

    // The canonical path: the part, the camera, the server — not the room.
    Assert.True((await AlarmAsync(s.Db, room.Input))!.alarm);
    Assert.Equal(1, (await AlarmAsync(s.Db, room.Camera))!.children_alarms);

    // The mirror on the reference replica carries the camera's row into the room's branch.
    var replica = await AlarmAsync(s.Db, room.Replica);
    Assert.NotNull(replica);
    Assert.Equal(1, replica!.children_alarms);
    Assert.Equal(1, (await AlarmAsync(s.Db, room.Floor))!.children_alarms);
    Assert.Equal(1, (await AlarmAsync(s.Db, room.Building))!.children_alarms);

    await s.Alarms.NormalAsync(room.Input, SensorOneKey, default);

    Assert.False((await AlarmAsync(s.Db, room.Input))!.alarm);
    Assert.Equal(0, (await AlarmAsync(s.Db, room.Replica))!.children_alarms);
    Assert.Equal(0, (await AlarmAsync(s.Db, room.Floor))!.children_alarms);
    Assert.Equal(0, (await AlarmAsync(s.Db, room.Building))!.children_alarms);
  }

  [Fact]
  public async Task DeletingTheCameraCascadesItsPartsAlarms()
  {
    await using var s = await OpenAsync();
    if (s is null) return;

    var tree = await SeedAsync(s);
    await s.Alarms.RaiseAsync(Sensor(tree.Input), default);

    // The camera's removal takes its subtree; the FK from alarms.object_id is what takes the
    // history with each node.
    await s.Updates.Delete([Utils.ConvertGuidToObjectId(tree.Camera)]);

    Assert.Empty(await s.Db.Alarms.AsNoTracking().ToListAsync());
    Assert.Empty(await s.Db.DeviceNodes.AsNoTracking().ToListAsync());
  }

  // ── A fault: the flag and its alarm are one transaction ──────────────────────────────────────

  [Fact]
  public async Task AFaultIsRaisedAndResetOnPostgres()
  {
    await using var s = await OpenAsync();
    if (s is null) return;

    var tree = await SeedAsync(s);
    var key = DeviceFaultService.DedupKey(tree.Input);

    var raised = await s.Alarms.RaiseAsync(Fault(tree.Input), default);
    Assert.Equal(AlarmMove.Applied, raised.Move);

    // A fault never writes ALARM and never counts as a child alarm.
    var nodeAlarm = await AlarmAsync(s.Db, tree.Input);
    Assert.True(nodeAlarm is null || !nodeAlarm.alarm);
    var cameraAlarm = await AlarmAsync(s.Db, tree.Camera);
    Assert.True(cameraAlarm is null || cameraAlarm.children_alarms == 0);

    await s.Alarms.NormalFaultAsync(tree.Input, key, default);
    var reset = await s.Alarms.CloseAsync(
      raised.Alarm!.id, AliceName, AlarmCloseReason.Reset, ResetComment, default);
    Assert.Equal(AlarmMove.Applied, reset.Move);

    var row = await s.Db.Alarms.AsNoTracking().SingleAsync();
    Assert.Equal(AlarmSource.Fault, row.source);
    Assert.Equal(AlarmCloseReason.Reset, row.close_reason);
    Assert.False(row.condition_active);
  }

  [Fact]
  public async Task APresentChangeAndItsFaultRollBackTogether()
  {
    await using var s = await OpenAsync();
    if (s is null) return;

    var tree = await SeedAsync(s);
    var faults = new DeviceFaultService(s.Db, s.Alarms);

    // The service joins the snapshot's transaction: rolling the snapshot back must take both the
    // presence flag and the fault with it.
    await using (var tx = await s.Db.Database.BeginTransactionAsync())
    {
      var node = await s.Db.DeviceNodes.SingleAsync(n => n.object_id == tree.Input);
      node.present = false;
      await faults.ApplyAsync([node], default);

      Assert.Single(await s.Db.Alarms.AsNoTracking()
        .Where(a => a.source == AlarmSource.Fault).ToListAsync());
      await tx.RollbackAsync();
    }

    s.Db.ChangeTracker.Clear();
    Assert.True((await s.Db.DeviceNodes.AsNoTracking()
      .SingleAsync(n => n.object_id == tree.Input)).present);
    Assert.Empty(await s.Db.Alarms.AsNoTracking()
      .Where(a => a.source == AlarmSource.Fault).ToListAsync());

    // The same pair commits together.
    await using (var tx = await s.Db.Database.BeginTransactionAsync())
    {
      var node = await s.Db.DeviceNodes.SingleAsync(n => n.object_id == tree.Input);
      node.present = false;
      await faults.ApplyAsync([node], default);
      await tx.CommitAsync();
    }

    Assert.False((await s.Db.DeviceNodes.AsNoTracking()
      .SingleAsync(n => n.object_id == tree.Input)).present);
    var fault = await s.Db.Alarms.AsNoTracking()
      .SingleAsync(a => a.source == AlarmSource.Fault);
    Assert.Equal(AlarmStatus.Active, fault.status);
    Assert.True(fault.condition_active);
  }

  [Fact]
  public async Task AResetRacingANewAbsenceLeavesTheFaultConsistentWithPresent()
  {
    await using var s = await OpenAsync();
    if (s is null) return;

    var tree = await SeedAsync(s);
    var key = DeviceFaultService.DedupKey(tree.Input);

    var raised = await s.Alarms.RaiseAsync(Fault(tree.Input), default);
    await s.Alarms.NormalFaultAsync(tree.Input, key, default); // returned; the part is present
    var faultId = raised.Alarm!.id;

    var second = SecondStore(s, out var otherDb);
    await using var _ = otherDb;

    // The snapshot side, on its own connection: mark the part absent and apply the fault rule in
    // one transaction. It races the operator resetting the returned fault.
    var snapshot = Task.Run(async () =>
    {
      var node = await otherDb.DeviceNodes.SingleAsync(n => n.object_id == tree.Input);
      await using var tx = await otherDb.Database.BeginTransactionAsync();
      node.present = false;
      await new DeviceFaultService(otherDb, second).ApplyAsync([node], default);
      await tx.CommitAsync();
    });

    var reset = s.Alarms.CloseAsync(
      faultId, AliceName, AlarmCloseReason.Reset, ResetComment, default);

    await Task.WhenAll(snapshot, reset);

    Assert.False((await s.Db.DeviceNodes.AsNoTracking()
      .SingleAsync(n => n.object_id == tree.Input)).present);

    // Whichever transaction committed first, exactly one open fault remains and it describes the
    // absence; a row closed by the reset never keeps a live condition.
    var open = await s.Db.Alarms.AsNoTracking()
      .Where(a => a.source == AlarmSource.Fault && a.status != AlarmStatus.Closed)
      .ToListAsync();
    Assert.Single(open);
    Assert.True(open[0].condition_active);

    var closed = await s.Db.Alarms.AsNoTracking()
      .Where(a => a.source == AlarmSource.Fault && a.status == AlarmStatus.Closed)
      .ToListAsync();
    Assert.All(closed, a => Assert.False(a.condition_active));
  }
}
