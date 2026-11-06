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
    var updates = new MapUpdateService(map, tracks, states);
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
      DeviceObjectId.CameraSource, TestCameras.One, DeviceObjectId.DigitalInputPart, "1"));

    s.Db.Markers.AddRange(
      new DBMarker { id = root, name = "Building" },
      new DBMarker { id = floor, name = "Floor", parent_id = root },
      new DBMarker { id = camera, name = "Camera", parent_id = floor },
      new DBMarker { id = input, name = "Door", parent_id = camera });
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
        part_token = "1",
      });
    await s.Db.SaveChangesAsync();

    return new Tree(root, floor, camera, input);
  }

  private static Task<DBAlarmState?> AlarmAsync(VmsMapDbContext db, Guid id) =>
    db.AlarmStates.AsNoTracking().FirstOrDefaultAsync(a => a.id == id);

  private static RaiseAlarmSpec Sensor(Guid input, string key = "sensor:1") =>
    new(input, AlarmSource.Sensor, key, Trigger: "open");

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
      s.Alarms.AcknowledgeAsync(raised.Alarm!.id, "alice", null, default),
      second.RaiseAsync(Sensor(tree.Input), default));

    var row = await s.Db.Alarms.AsNoTracking().SingleAsync();
    Assert.Equal(AlarmStatus.Acknowledged, row.status);
    Assert.True(row.condition_active);
    Assert.True((await AlarmAsync(s.Db, tree.Input))!.alarm);
  }

  [Fact]
  public async Task ALIVEAlarmOnAnotherChildKeepsTheAncestorCounted()
  {
    await using var s = await OpenAsync();
    if (s is null) return;

    var tree = await SeedAsync(s);

    var otherCamera = Guid.Parse(CameraObjectId.For(TestCameras.Two));
    var otherInput = Guid.Parse(DeviceObjectId.For(
      DeviceObjectId.CameraSource, TestCameras.Two, DeviceObjectId.DigitalInputPart, "1"));
    s.Db.Markers.AddRange(
      new DBMarker { id = otherCamera, name = "Other camera", parent_id = tree.Floor },
      new DBMarker { id = otherInput, name = "Other door", parent_id = otherCamera });
    await s.Db.SaveChangesAsync();

    s.Db.DeviceNodes.AddRange(
      TestCameras.DeviceNode(TestCameras.Two, otherCamera),
      new DBDeviceNode
      {
        object_id = otherInput,
        source_kind = DeviceObjectId.CameraSource,
        source_id = TestCameras.Two,
        part_kind = DeviceObjectId.DigitalInputPart,
        part_token = "1",
      });
    await s.Db.SaveChangesAsync();

    await s.Alarms.RaiseAsync(Sensor(tree.Input, "sensor:one"), default);
    await s.Alarms.RaiseAsync(Sensor(otherInput, "sensor:two"), default);
    Assert.Equal(2, (await AlarmAsync(s.Db, tree.Floor))!.children_alarms);

    await s.Alarms.NormalAsync(otherInput, "sensor:two", default);

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

    await s.Alarms.AcknowledgeAsync(raised.Alarm!.id, "alice", null, default);

    var after = await s.Db.AlarmStates.AsNoTracking()
      .ToDictionaryAsync(a => a.id, a => (a.alarm, a.children_alarms));
    Assert.Equal(before, after);
    Assert.True((await AlarmAsync(s.Db, tree.Input))!.alarm);
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
}
