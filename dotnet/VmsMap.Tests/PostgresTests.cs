using Microsoft.EntityFrameworkCore;
using Npgsql;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using VmsDb;
using VmsMap.Db;
using VmsMap.Domain;

namespace VmsMap.Tests;

// The parts that only a real PostgreSQL with PostGIS can check: the recursive CTEs, the
// transaction around an update and the cascade on delete. Enabled by pointing VMS_MAP_TEST_DSN
// at a scratch database (`docker compose up -d postgres`, then
// `createdb ta_map_test`); skipped otherwise, because the Windows CI runner has no PostgreSQL.
[Collection("postgis")]
public class PostgresTests
{  private static readonly string? Dsn =
    Environment.GetEnvironmentVariable("VMS_MAP_TEST_DSN");

  private sealed record Services(
    NpgsqlDataSource DataSource,
    VmsMapDbContext Db,
    MapDb Map,
    StatesUpdateService States,
    MapUpdateService Updates) : IAsyncDisposable
  {
    public async ValueTask DisposeAsync()
    {
      await Db.DisposeAsync();
      await DataSource.DisposeAsync();
    }
  }

  private static async Task<Services?> OpenAsync()
  {
    if (string.IsNullOrWhiteSpace(Dsn))
    {
      return null;
    }

    var dataSourceBuilder = new NpgsqlDataSourceBuilder(Dsn);
    dataSourceBuilder.EnableDynamicJson();
    dataSourceBuilder.UseNetTopologySuite();
    var dataSource = dataSourceBuilder.Build();

    await new VmsDbMigrator(dataSource, "VmsMapTest", AppContext.BaseDirectory, "7743512881")
      .MigrateAsync();

    var options = new DbContextOptionsBuilder<VmsMapDbContext>()
      .UseNpgsql(dataSource, o => o.UseNetTopologySuite())
      .Options;

    var db = new VmsMapDbContext(options);

    // A test starts from an empty tree; the side tables follow through their keys.
    await db.Database.ExecuteSqlRawAsync("TRUNCATE objects CASCADE;");

    var map = new MapDb(db);
    var stateService = new StateService(db);
    var states = new StatesUpdateService(stateService, map, db);
    var levels = new LevelService(db);
    var trackService = new TrackService(db, levels);
    var geoService = new GeoService(db, levels);
    var tracks = new TracksUpdateService(map, trackService, geoService);
    var updates = new MapUpdateService(map, tracks, states);

    return new Services(dataSource, db, map, states, updates);
  }

  private static async Task<(Guid Root, Guid Floor, Guid Leaf)> SeedTreeAsync(VmsMapDbContext db)
  {
    var root = Guid.NewGuid();
    var floor = Guid.NewGuid();
    var leaf = Guid.NewGuid();

    db.Markers.AddRange(
      new DBMarker { id = root, name = "Building" },
      new DBMarker { id = floor, name = "Floor", parent_id = root },
      new DBMarker { id = leaf, name = "Camera", parent_id = floor });

    await db.SaveChangesAsync();
    return (root, floor, leaf);
  }

  private static Task<DBAlarmState?> AlarmAsync(VmsMapDbContext db, Guid id) =>
    db.AlarmStates.AsNoTracking().FirstOrDefaultAsync(a => a.id == id);

  [Fact]
  public async Task AnAlarmOnALeafReachesTheRootAndClearsAgain()
  {
    await using var s = await OpenAsync();
    if (s is null) return;

    var (root, floor, leaf) = await SeedTreeAsync(s.Db);

    await s.States.UpdateStates(new List<ObjectStateDTO>
    {
      new() { id = Utils.ConvertGuidToObjectId(leaf), states = new List<string> { "ALARM" } }
    });

    Assert.True((await AlarmAsync(s.Db, leaf))!.alarm);
    Assert.Equal(1, (await AlarmAsync(s.Db, floor))!.children_alarms);
    Assert.Equal(1, (await AlarmAsync(s.Db, root))!.children_alarms);

    await s.States.UpdateStates(new List<ObjectStateDTO>
    {
      new() { id = Utils.ConvertGuidToObjectId(leaf), states = new List<string> { "NORM" } }
    });

    Assert.False((await AlarmAsync(s.Db, leaf))!.alarm);
    Assert.Equal(0, (await AlarmAsync(s.Db, floor))!.children_alarms);
    Assert.Equal(0, (await AlarmAsync(s.Db, root))!.children_alarms);
  }

  [Fact]
  public async Task RepeatingTheSameStateKeepsTheCounters()
  {
    await using var s = await OpenAsync();
    if (s is null) return;

    var (root, floor, leaf) = await SeedTreeAsync(s.Db);

    var alarm = new List<ObjectStateDTO>
    {
      new() { id = Utils.ConvertGuidToObjectId(leaf), states = new List<string> { "ALARM" } }
    };

    await s.States.UpdateStates(alarm);
    await s.States.UpdateStates(alarm);

    Assert.Equal(1, (await AlarmAsync(s.Db, floor))!.children_alarms);
    Assert.Equal(1, (await AlarmAsync(s.Db, root))!.children_alarms);
  }

  [Fact]
  public async Task DeletingAnAlarmedLeafLowersItsParent()
  {
    await using var s = await OpenAsync();
    if (s is null) return;

    var (root, floor, leaf) = await SeedTreeAsync(s.Db);

    await s.States.UpdateStates(new List<ObjectStateDTO>
    {
      new() { id = Utils.ConvertGuidToObjectId(leaf), states = new List<string> { "ALARM" } }
    });

    await s.Updates.Delete(new List<string> { Utils.ConvertGuidToObjectId(leaf) });

    Assert.Null(await s.Db.Markers.FirstOrDefaultAsync(m => m.id == leaf));
    Assert.Null(await AlarmAsync(s.Db, leaf));
    Assert.Equal(0, (await AlarmAsync(s.Db, floor))!.children_alarms);
    Assert.Equal(0, (await AlarmAsync(s.Db, root))!.children_alarms);
  }

  [Fact]
  public async Task MovingAnAlarmedNodeRecomputesBothParents()
  {
    await using var s = await OpenAsync();
    if (s is null) return;

    var (root, floor, leaf) = await SeedTreeAsync(s.Db);

    var otherRoot = Guid.NewGuid();
    s.Db.Markers.Add(new DBMarker { id = otherRoot, name = "Other building" });
    await s.Db.SaveChangesAsync();

    await s.States.UpdateStates(new List<ObjectStateDTO>
    {
      new() { id = Utils.ConvertGuidToObjectId(leaf), states = new List<string> { "ALARM" } }
    });

    Assert.Equal(1, (await AlarmAsync(s.Db, floor))!.children_alarms);

    await s.Updates.UpdateObjects(new List<BaseMarkerDTO>
    {
      new()
      {
        id = Utils.ConvertGuidToObjectId(leaf),
        name = "Camera",
        parent_id = Utils.ConvertGuidToObjectId(otherRoot)
      }
    });

    Assert.Equal(0, (await AlarmAsync(s.Db, floor))!.children_alarms);
    Assert.Equal(1, (await AlarmAsync(s.Db, otherRoot))!.children_alarms);
    Assert.Equal(0, (await AlarmAsync(s.Db, root))!.children_alarms);
  }

  [Fact]
  public async Task DeletingANodeRemovesEverythingKeyedToIt()
  {
    await using var s = await OpenAsync();
    if (s is null) return;

    var (_, _, leaf) = await SeedTreeAsync(s.Db);

    s.Db.Properties.Add(new DBMarkerProp
    {
      id = Guid.NewGuid(),
      prop_name = "serial",
      str_val = "1",
      object_id = leaf
    });
    s.Db.Events.Add(new DBEvent
    {
      id = Guid.NewGuid(),
      object_id = leaf,
      event_name = "alarm",
      timestamp = DateTime.UtcNow
    });
    s.Db.Rights.Add(new DBRight { object_id = leaf, subject = "admin", action = "view" });
    s.Db.Visuals.Add(new DBVisual { object_id = leaf, color = "#fff" });
    await s.Db.SaveChangesAsync();

    await s.Updates.Delete(new List<string> { Utils.ConvertGuidToObjectId(leaf) });

    Assert.Empty(await s.Db.Properties.Where(p => p.object_id == leaf).ToListAsync());
    Assert.Empty(await s.Db.Events.Where(e => e.object_id == leaf).ToListAsync());
    Assert.Empty(await s.Db.Rights.Where(r => r.object_id == leaf).ToListAsync());
    Assert.Empty(await s.Db.Visuals.Where(v => v.object_id == leaf).ToListAsync());
  }
}
