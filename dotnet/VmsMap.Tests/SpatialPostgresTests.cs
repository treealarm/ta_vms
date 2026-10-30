using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Vms.Map;
using VmsDb;
using VmsMap.Clients;
using VmsMap.Db;
using VmsMap.Domain;
using VmsMap.PubSub;

namespace VmsMap.Tests;

// The spatial paths that only a real PostgreSQL with PostGIS can check: the recursive delete of a
// space's subtree, the cascades behind it, the partial unique index on the reference placement and
// the transaction around a placement write. Enabled by VMS_MAP_TEST_DSN, as PostgresTests is.
[Collection("postgis")]
public class SpatialPostgresTests
{  private static readonly string? Dsn = Environment.GetEnvironmentVariable("VMS_MAP_TEST_DSN");

  private sealed class FakeCatalog : ICameraCatalog
  {
    public Task<CameraInfo?> GetAsync(string cameraId, CancellationToken ct) =>
      Task.FromResult<CameraInfo?>(new CameraInfo(cameraId, "Camera " + cameraId));
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
    NpgsqlDataSource DataSource, VmsMapDbContext Db, SpatialDb Spatial, RecordingNotifier Notifier)
    : IAsyncDisposable
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
      return null;

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
    await db.Database.ExecuteSqlRawAsync("TRUNCATE objects CASCADE;");

    var map = new MapDb(db);
    var stateService = new StateService(db);
    var states = new StatesUpdateService(stateService, map, db);
    var levels = new LevelService(db);
    var trackService = new TrackService(db, levels);
    var geo = new GeoService(db, levels);
    var tracks = new TracksUpdateService(map, trackService, geo);
    var updates = new MapUpdateService(map, tracks, states);
    var notifier = new RecordingNotifier();

    return new Services(
      dataSource, db, new SpatialDb(db, map, updates, geo, new FakeCatalog(), notifier), notifier);
  }

  private static Space MapSpace(string name = "Main map") => new()
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
  public async Task RemovingASpaceDeletesItsSubtreeAndDetachesTheStandingCamera()
  {
    await using var test = await OpenAsync();
    if (test is null)
      return;

    var map = await test.Spatial.UpsertSpaceAsync(MapSpace(), CancellationToken.None);
    var plan = await test.Spatial.UpsertSpaceAsync(PlanSpace("Floor", map.Id), CancellationToken.None);
    await test.Spatial.UpsertPlacementAsync(Placement("cam1", plan.Id, reference: true), CancellationToken.None);
    await test.Spatial.UpsertPlacementAsync(Placement("cam1", map.Id, reference: false), CancellationToken.None);

    var landmark = await test.Spatial.UpsertLandmarkAsync(new Landmark
    {
      SpaceId = plan.Id,
      Name = "Corner",
      Lat = 0.4,
      Lon = 0.4,
      HeightM = 2,
    }, CancellationToken.None);
    var observations = new PtzGeoObservations { CameraId = "cam1" };
    observations.Observations.Add(new PtzGeoObservation { LandmarkId = landmark.Id, Pan = 10, Tilt = -5 });
    await test.Spatial.SetObservationsAsync(observations, CancellationToken.None);

    Assert.True(await test.Spatial.RemoveSpaceAsync(Guid.Parse(plan.Id), CancellationToken.None));

    Assert.False(await test.Db.Spaces.AnyAsync(s => s.object_id == Guid.Parse(plan.Id)));
    Assert.False(await test.Db.Landmarks.AnyAsync());
    Assert.False(await test.Db.PtzGeoObservations.AnyAsync());

    // The camera stops standing there but its node stays, for the picture on the map.
    var canonical = Guid.Parse(CameraObjectId.For("cam1"));
    Assert.Null((await test.Db.Markers.SingleAsync(m => m.id == canonical)).parent_id);
    Assert.False(await test.Db.CameraPlacements.AnyAsync(p => p.is_reference));
    var picture = await test.Db.CameraPlacements.SingleAsync(p => p.camera_id == "cam1");
    Assert.Equal(Guid.Parse(map.Id), (await test.Db.Markers.SingleAsync(m => m.id == picture.object_id)).parent_id);
    Assert.Contains("cam1", test.Notifier.Events[^1].Cameras);
  }

  [Fact]
  public async Task TheReferenceIndexAdmitsOneReferencePerCamera()
  {
    await using var test = await OpenAsync();
    if (test is null)
      return;

    var map = await test.Spatial.UpsertSpaceAsync(MapSpace(), CancellationToken.None);
    var spaceId = Guid.Parse(map.Id);

    var first = Guid.NewGuid();
    test.Db.Markers.Add(new DBMarker { id = first, parent_id = spaceId, name = "Camera" });
    await test.Db.SaveChangesAsync();
    test.Db.CameraPlacements.Add(new DBCameraPlacement
    {
      object_id = first, camera_id = "cam1", is_reference = true,
    });
    await test.Db.SaveChangesAsync();

    var second = Guid.NewGuid();
    test.Db.Markers.Add(new DBMarker { id = second, parent_id = spaceId, name = "Camera" });
    await test.Db.SaveChangesAsync();
    test.Db.CameraPlacements.Add(new DBCameraPlacement
    {
      object_id = second, camera_id = "cam1", is_reference = true,
    });
    await Assert.ThrowsAsync<DbUpdateException>(() => test.Db.SaveChangesAsync());
  }

  [Fact]
  public async Task GeometryIsPostGisAndTheCanvasIsRecorded()
  {
    await using var test = await OpenAsync();
    if (test is null)
      return;

    var map = await test.Spatial.UpsertSpaceAsync(MapSpace(), CancellationToken.None);
    var plan = PlanSpace("Floor", map.Id);
    plan.Footprint.AddRange([
      new Coordinate { Lat = 0.1, Lon = 0.1 },
      new Coordinate { Lat = 0.2, Lon = 0.1 },
      new Coordinate { Lat = 0.1, Lon = 0.2 },
    ]);
    var saved = await test.Spatial.UpsertSpaceAsync(plan, CancellationToken.None);
    await test.Spatial.UpsertPlacementAsync(Placement("cam1", saved.Id, reference: true), CancellationToken.None);

    var planId = Guid.Parse(saved.Id);
    var cameraId = Guid.Parse(CameraObjectId.For("cam1"));

    var types = await test.Db.Database
      .SqlQuery<string>($"SELECT ST_GeometryType(figure) AS \"Value\" FROM geo_objects WHERE id = {planId}")
      .ToListAsync();
    Assert.Equal("ST_Polygon", Assert.Single(types));

    var point = await test.Db.Database
      .SqlQuery<string>($"SELECT ST_GeometryType(figure) AS \"Value\" FROM geo_objects WHERE id = {cameraId}")
      .SingleAsync();
    Assert.Equal("ST_Point", point);

    var canvases = await (from g in test.Db.GeoObjects
                          where g.id == planId || g.id == cameraId
                          select new { g.id, g.canvas_id }).ToListAsync();
    Assert.Equal(Guid.Parse(map.Id), canvases.Single(c => c.id == planId).canvas_id);
    Assert.Equal(planId, canvases.Single(c => c.id == cameraId).canvas_id);
  }

  [Fact]
  public async Task PromotingTheReferenceSwapsItWithoutTrippingTheIndex()
  {
    await using var test = await OpenAsync();
    if (test is null)
      return;

    var map = await test.Spatial.UpsertSpaceAsync(MapSpace(), CancellationToken.None);
    var plan = await test.Spatial.UpsertSpaceAsync(PlanSpace("Floor", map.Id), CancellationToken.None);
    await test.Spatial.UpsertPlacementAsync(Placement("cam1", map.Id, reference: true), CancellationToken.None);
    await test.Spatial.UpsertPlacementAsync(Placement("cam1", plan.Id, reference: false), CancellationToken.None);

    // The unique index admits one reference at a time; the write must demote the old one first.
    await test.Spatial.UpsertPlacementAsync(Placement("cam1", plan.Id, reference: true), CancellationToken.None);

    var rows = await test.Db.CameraPlacements.Where(p => p.camera_id == "cam1").ToListAsync();
    Assert.Equal(2, rows.Count);
    var reference = rows.Single(r => r.is_reference);
    Assert.Equal(Guid.Parse(plan.Id),
      (await test.Db.Markers.SingleAsync(m => m.id == reference.object_id)).parent_id!.Value);

    var placements = await test.Spatial.ListPlacementsAsync(CancellationToken.None);
    Assert.Equal(2, placements.Count);
    Assert.Single(placements, p => p.IsReference);
  }
}
