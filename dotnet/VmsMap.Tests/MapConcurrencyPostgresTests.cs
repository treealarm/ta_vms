using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Vms.Map;
using VmsDb;
using VmsMap.Adapter;
using VmsMap.Clients;
using VmsMap.Db;
using VmsMap.Domain;
using VmsMap.PubSub;

namespace VmsMap.Tests;

// The two races the tree has to survive: two opposite moves that would close a cycle, and two
// placements of one camera on one canvas. Both need two connections to the same database, which is
// also what two replicas of VmsMap look like. Enabled by VMS_MAP_TEST_DSN, as the other Postgres
// tests are; the collection keeps them from running beside each other.
[Collection("postgis")]
public class MapConcurrencyPostgresTests
{
  private static readonly string? Dsn = Environment.GetEnvironmentVariable("VMS_MAP_TEST_DSN");

  // The fixtures these tests share.
  private const string MainMapName = "main map";
  private const string FloorPlanName = "floor";
  private const string RootAName = "root a";
  private const string RootBName = "root b";
  private const string NodeAName = "a";
  private const string NodeBName = "b";

  private sealed class FakeCatalog : ICameraCatalog
  {
    public Task<CameraInfo?> GetAsync(string cameraId, CancellationToken ct) =>
      Task.FromResult<CameraInfo?>(new CameraInfo(cameraId, "Camera " + cameraId, "srv", false));

    public Task<List<CameraInfo>> ListAsync(string? serverId, CancellationToken ct) =>
      Task.FromResult(new List<CameraInfo>());
  }

  private sealed class RecordingNotifier : IMapConfigNotifier
  {
    public List<(IReadOnlyCollection<string> Objects, IReadOnlyCollection<string> Cameras)> Events { get; } = [];

    public Task PublishAsync(
      IReadOnlyCollection<string> objectIds, IReadOnlyCollection<string> cameraIds, CancellationToken ct)
    {
      Events.Add((objectIds, cameraIds));
      return Task.CompletedTask;
    }
  }

  private sealed record Services(
    NpgsqlDataSource DataSource, VmsMapDbContext Db, MapDb Map,
    MapUpdateService Updates, SpatialDb Spatial, RecordingNotifier Notifier)
    : IAsyncDisposable
  {
    public async ValueTask DisposeAsync()
    {
      await Db.DisposeAsync();
      await DataSource.DisposeAsync();
    }
  }

  /// <summary>
  /// One service instance. The first call migrates and empties the shared scratch database; the
  /// second, parallel one joins it with a data source and context of its own — two connections,
  /// which is what the advisory locks can be observed on.
  /// </summary>
  private static async Task<Services?> OpenAsync(bool fresh)
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
    if (fresh)
      await db.Database.ExecuteSqlRawAsync("TRUNCATE objects CASCADE;");

    var map = new MapDb(db);
    var stateService = new StateService(db);
    var states = new StatesUpdateService(stateService, map, db);
    var levels = new LevelService(db);
    var trackService = new TrackService(db, levels);
    var geo = new GeoService(db, levels);
    var tracks = new TracksUpdateService(map, trackService, geo);
    var updates = new MapUpdateService(db, map, tracks, states);
    var notifier = new RecordingNotifier();
    var spatial = new SpatialDb(
      db, map, updates, geo, new FakeCatalog(), notifier, TestWiring.CameraArming(db, map));

    return new Services(dataSource, db, map, updates, spatial, notifier);
  }

  private static async Task<Exception?> Capture(Func<Task> action)
  {
    try
    {
      await action();
      return null;
    }
    catch (Exception ex)
    {
      return ex;
    }
  }

  private static Space MapSpace(string name = MainMapName) => new()
  {
    Name = name,
    Kind = SpaceKind.Map,
    RealCoordinates = true,
    View = new GeoBounds
    {
      SouthWest = new Coordinate { Lat = 0, Lon = 0 },
      NorthEast = new Coordinate { Lat = 1, Lon = 1 },
    },
  };

  private static Space PlanSpace(string name, string parentId) => new()
  {
    Name = name,
    ParentId = parentId,
    Kind = SpaceKind.Plan,
    RealCoordinates = true,
    View = new GeoBounds
    {
      SouthWest = new Coordinate { Lat = 0, Lon = 0 },
      NorthEast = new Coordinate { Lat = 1, Lon = 1 },
    },
  };

  private static CameraPlacement Placement(
    string cameraId, string spaceId, bool reference, double lat = 0.5, double lon = 0.5) => new()
  {
    CameraId = cameraId,
    SpaceId = spaceId,
    IsReference = reference,
    Lat = lat,
    Lon = lon,
    HeightM = 3,
    HeadingDeg = 90,
    HfovDeg = 60,
    RangeM = 30,
  };

  [Fact]
  public async Task TwoOppositeMovesCannotCloseACycle()
  {
    await using var first = await OpenAsync(true);
    if (first is null)
      return;
    await using var second = await OpenAsync(false);
    if (second is null)
      return;

    var rootA = Guid.NewGuid();
    var rootB = Guid.NewGuid();
    var a = Guid.NewGuid();
    var b = Guid.NewGuid();
    first.Db.Markers.AddRange(
      new DBMarker { id = rootA, name = RootAName },
      new DBMarker { id = rootB, name = RootBName },
      new DBMarker { id = a, parent_id = rootA, name = NodeAName },
      new DBMarker { id = b, parent_id = rootB, name = NodeBName });
    await first.Db.SaveChangesAsync();

    var outcomes = await Task.WhenAll(
      Capture(() => first.Updates.UpdateObjects([
        new BaseMarkerDTO { id = a.ToString(), parent_id = b.ToString(), name = NodeAName }])),
      Capture(() => second.Updates.UpdateObjects([
        new BaseMarkerDTO { id = b.ToString(), parent_id = a.ToString(), name = NodeBName }])));

    // One move wins; the other sees the committed tree and refuses. Both committing would leave a
    // cycle the alarm aggregation cannot walk.
    Assert.Equal(1, outcomes.Count(outcome => outcome is null));
    Assert.Equal(1, outcomes.Count(outcome => outcome is ArgumentException));

    var parents = await first.Db.Markers.AsNoTracking()
      .ToDictionaryAsync(marker => marker.id, marker => marker.parent_id);
    foreach (var start in new[] { a, b })
    {
      var seen = new HashSet<Guid>();
      var at = start;
      while (parents.TryGetValue(at, out var parent) && parent is not null)
      {
        Assert.True(seen.Add(at), $"a cycle runs through '{at}'");
        at = parent.Value;
      }
    }
  }

  [Fact]
  public async Task TwoPlacementsOnOneCanvasLeaveOnePicture()
  {
    await using var first = await OpenAsync(true);
    if (first is null)
      return;
    await using var second = await OpenAsync(false);
    if (second is null)
      return;

    var map = await first.Spatial.UpsertSpaceAsync(MapSpace(), CancellationToken.None);
    var plan = await first.Spatial.UpsertSpaceAsync(PlanSpace(FloorPlanName, map.Id), CancellationToken.None);

    // A reference on another canvas: the new pictures are ordinary ones, so the unique index on
    // the reference admits both writes and cannot be what saves the invariant.
    await first.Spatial.UpsertPlacementAsync(
      Placement(TestCameras.One, map.Id, reference: true), CancellationToken.None);

    var outcomes = await Task.WhenAll(
      Capture(() => first.Spatial.UpsertPlacementAsync(
        Placement(TestCameras.One, plan.Id, reference: false), CancellationToken.None)),
      Capture(() => second.Spatial.UpsertPlacementAsync(
        Placement(TestCameras.One, plan.Id, reference: false), CancellationToken.None)));

    Assert.All(outcomes, outcome => Assert.Null(outcome));

    // One picture on the map and one on the plan: the second write found the row the first made.
    var rows = await first.Db.CameraPlacements.AsNoTracking()
      .Where(placement => placement.camera_id == TestCameras.One)
      .ToListAsync();
    Assert.Equal(2, rows.Count);

    var onPlan = await (from placement in first.Db.CameraPlacements.AsNoTracking()
                        join figure in first.Db.GeoObjects.AsNoTracking()
                          on placement.object_id equals figure.id
                        where placement.camera_id == TestCameras.One
                              && figure.canvas_id == Guid.Parse(plan.Id)
                        select placement).ToListAsync();
    Assert.Single(onPlan);
  }
}
