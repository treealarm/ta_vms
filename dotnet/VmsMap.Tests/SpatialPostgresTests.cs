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

// The spatial paths that only a real PostgreSQL with PostGIS can check: the recursive delete of a
// space's subtree, the cascades behind it, the partial unique index on the reference placement and
// the transaction around a placement write. Enabled by VMS_MAP_TEST_DSN, as PostgresTests is.
[Collection("postgis")]
public class SpatialPostgresTests
{  private static readonly string? Dsn = Environment.GetEnvironmentVariable("VMS_MAP_TEST_DSN");

  private sealed class FakeCatalog(IReadOnlySet<string>? geoFromDevice = null) : ICameraCatalog
  {
    public Task<CameraInfo?> GetAsync(string cameraId, CancellationToken ct) =>
      Task.FromResult<CameraInfo?>(new CameraInfo(
        cameraId, "Camera " + cameraId, "srv", geoFromDevice?.Contains(cameraId) == true));

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
    NpgsqlDataSource DataSource, VmsMapDbContext Db, MapDb Map, MapUpdateService Updates,
    SpatialDb Spatial, RecordingNotifier Notifier)
    : IAsyncDisposable
  {
    public async ValueTask DisposeAsync()
    {
      await Db.DisposeAsync();
      await DataSource.DisposeAsync();
    }
  }

  private static async Task<Services?> OpenAsync(IReadOnlySet<string>? geoFromDevice = null)
  {
    if (string.IsNullOrWhiteSpace(Dsn))
      return null;

    // The migration creates PostGIS, and Npgsql loads a database's types when a data source first
    // connects: migrate on a data source of its own, then build the one the context uses.
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
      dataSource, db, map, updates,
      new SpatialDb(db, map, updates, geo, new FakeCatalog(geoFromDevice), notifier), notifier);
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
    await test.Spatial.UpsertPlacementAsync(Placement(TestCameras.One, plan.Id, reference: true), CancellationToken.None);
    await test.Spatial.UpsertPlacementAsync(Placement(TestCameras.One, map.Id, reference: false), CancellationToken.None);

    var landmark = await test.Spatial.UpsertLandmarkAsync(new Landmark
    {
      SpaceId = plan.Id,
      Name = "Corner",
      Lat = 0.4,
      Lon = 0.4,
      HeightM = 2,
    }, CancellationToken.None);
    var observations = new PtzGeoObservations { CameraId = TestCameras.One };
    observations.Observations.Add(new PtzGeoObservation { LandmarkId = landmark.Id, Pan = 10, Tilt = -5 });
    await test.Spatial.SetObservationsAsync(observations, CancellationToken.None);

    Assert.True(await test.Spatial.RemoveSpaceAsync(Guid.Parse(plan.Id), CancellationToken.None));

    Assert.False(await test.Db.Spaces.AnyAsync(s => s.object_id == Guid.Parse(plan.Id)));
    Assert.False(await test.Db.Landmarks.AnyAsync());
    Assert.False(await test.Db.PtzGeoObservations.AnyAsync());

    // The camera stops standing there but its node stays, for the picture on the map.
    var canonical = Guid.Parse(CameraObjectId.For(TestCameras.One));
    Assert.Null((await test.Db.Markers.SingleAsync(m => m.id == canonical)).parent_id);
    Assert.False(await test.Db.CameraPlacements.AnyAsync(p => p.is_reference));
    var picture = await test.Db.CameraPlacements.SingleAsync(p => p.camera_id == TestCameras.One);
    Assert.Equal(Guid.Parse(map.Id), (await test.Db.Markers.SingleAsync(m => m.id == picture.object_id)).parent_id);
    Assert.Contains(TestCameras.One, test.Notifier.Events[^1].Cameras);
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
      object_id = first, camera_id = TestCameras.One, is_reference = true,
    });
    await test.Db.SaveChangesAsync();

    var second = Guid.NewGuid();
    test.Db.Markers.Add(new DBMarker { id = second, parent_id = spaceId, name = "Camera" });
    await test.Db.SaveChangesAsync();
    test.Db.CameraPlacements.Add(new DBCameraPlacement
    {
      object_id = second, camera_id = TestCameras.One, is_reference = true,
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
    await test.Spatial.UpsertPlacementAsync(Placement(TestCameras.One, saved.Id, reference: true), CancellationToken.None);

    var planId = Guid.Parse(saved.Id);
    var cameraId = Guid.Parse(CameraObjectId.For(TestCameras.One));

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
    await test.Spatial.UpsertPlacementAsync(Placement(TestCameras.One, map.Id, reference: true), CancellationToken.None);
    await test.Spatial.UpsertPlacementAsync(Placement(TestCameras.One, plan.Id, reference: false), CancellationToken.None);

    // The unique index admits one reference at a time; the write must demote the old one first.
    await test.Spatial.UpsertPlacementAsync(Placement(TestCameras.One, plan.Id, reference: true), CancellationToken.None);

    var rows = await test.Db.CameraPlacements.Where(p => p.camera_id == TestCameras.One).ToListAsync();
    Assert.Equal(2, rows.Count);
    var reference = rows.Single(r => r.is_reference);
    Assert.Equal(Guid.Parse(plan.Id),
      (await test.Db.Markers.SingleAsync(m => m.id == reference.object_id)).parent_id!.Value);

    var placements = await test.Spatial.ListPlacementsAsync(CancellationToken.None);
    Assert.Equal(2, placements.Count);
    Assert.Single(placements, p => p.IsReference);
  }

  [Fact]
  public async Task ADeviceReportedPointMovesTheNodeAndNothingElse()
  {
    await using var test = await OpenAsync();
    if (test is null)
      return;

    var map = await test.Spatial.UpsertSpaceAsync(MapSpace(), CancellationToken.None);
    await test.Spatial.UpsertPlacementAsync(Placement(TestCameras.One, map.Id, reference: true, 0.5, 0.5), CancellationToken.None);

    var canonicalId = Guid.Parse(CameraObjectId.For(TestCameras.One));
    var before = await test.Db.CameraPlacements.AsNoTracking().SingleAsync(p => p.object_id == canonicalId);
    var parentBefore = (await test.Db.Markers.AsNoTracking().SingleAsync(m => m.id == canonicalId)).parent_id;

    var moved = await test.Spatial.MoveReferencePointAsync(TestCameras.One, 0.61, 0.71, CancellationToken.None);

    Assert.True(moved);
    var figure = await test.Db.GeoObjects.AsNoTracking().SingleAsync(g => g.id == canonicalId);
    var point = (NetTopologySuite.Geometries.Point)figure.figure;
    Assert.Equal(0.71, point.X, 6); // longitude
    Assert.Equal(0.61, point.Y, 6); // latitude

    // The device says where it is, not how it is mounted: the values an administrator set stay.
    var after = await test.Db.CameraPlacements.AsNoTracking().SingleAsync(p => p.object_id == canonicalId);
    Assert.Equal(before.height_m, after.height_m);
    Assert.Equal(before.heading_deg, after.heading_deg);
    Assert.Equal(before.hfov_deg, after.hfov_deg);
    Assert.Equal(before.range_m, after.range_m);
    Assert.Equal(parentBefore, (await test.Db.Markers.AsNoTracking().SingleAsync(m => m.id == canonicalId)).parent_id);

    // The map's watch stream and the tracking models both hear about it.
    Assert.Contains(TestCameras.One, test.Notifier.Events[^1].Cameras);
  }

  [Fact]
  public async Task AnUnplacedCameraHasNowhereToMove()
  {
    await using var test = await OpenAsync();
    if (test is null)
      return;

    Assert.False(await test.Spatial.MoveReferencePointAsync(TestCameras.One, 0.61, 0.71, CancellationToken.None));
  }

  [Fact]
  public async Task ADeviceFollowingCameraRefusesACalibration()
  {
    await using var test = await OpenAsync(new HashSet<string>(StringComparer.Ordinal) { TestCameras.One });
    if (test is null)
      return;

    var map = await test.Spatial.UpsertSpaceAsync(MapSpace(), CancellationToken.None);
    await test.Spatial.UpsertPlacementAsync(Placement(TestCameras.One, map.Id, reference: true), CancellationToken.None);
    var landmark = await test.Spatial.UpsertLandmarkAsync(new Landmark
    {
      SpaceId = map.Id,
      Name = "Corner",
      Lat = 0.4,
      Lon = 0.4,
      HeightM = 2,
    }, CancellationToken.None);

    var marks = new GroundObservations { CameraId = TestCameras.One };
    marks.Observations.Add(new GroundObservation { LandmarkId = landmark.Id, X = 0.5f, Y = 0.5f });
    var ground = await Assert.ThrowsAsync<SpatialConfigException>(
      () => test.Spatial.SetGroundObservationsAsync(marks, CancellationToken.None));
    Assert.Contains("position from the device", ground.Message);

    // The same rule for the PTZ landmark observations: both describe a fixed standpoint.
    var observations = new PtzGeoObservations { CameraId = TestCameras.One };
    observations.Observations.Add(new PtzGeoObservation { LandmarkId = landmark.Id, Pan = 10, Tilt = -5 });
    await Assert.ThrowsAsync<SpatialConfigException>(
      () => test.Spatial.SetObservationsAsync(observations, CancellationToken.None));
  }

  // A brand-new camera's node and its registry row used to be written in one SaveChanges, and EF
  // ordered the inserts by tracking order — the row first, straight into its foreign key. Only
  // PostgreSQL notices, which is why this lives here. Found live: adding `sim-vehicle` made the
  // whole demo install fail.
  [Fact]
  public async Task ANewCamerasNodeIsWrittenBeforeItsRegistryRow()
  {
    await using var test = await OpenAsync();
    if (test is null)
      return;

    var nodes = new DeviceNodeService(
      test.Db, test.Map, test.Updates, NullLogger<DeviceNodeService>.Instance);
    await nodes.EnsureCameraAsync(new CameraInfo(TestCameras.Two, "New camera", "srv-new"), CancellationToken.None);

    var canonical = Guid.Parse(CameraObjectId.For(TestCameras.Two));
    Assert.NotNull(await test.Db.Markers.AsNoTracking().FirstOrDefaultAsync(m => m.id == canonical));
    Assert.Equal(TestCameras.Two, (await test.Db.DeviceNodes.AsNoTracking()
      .SingleAsync(n => n.object_id == canonical)).source_id);
  }

  // The parts are nodes of their own under the camera, and the schema cascades everything a node
  // owns -- states, alarms, events, the registry row -- when the subtree goes. Only PostgreSQL can
  // show that, because the deleted node's whole subtree is removed by the database, not by EF.
  [Fact]
  public async Task DeletingACameraTakesItsPartsTheirStatesEventsAndAlarms()
  {
    await using var test = await OpenAsync();
    if (test is null)
      return;

    var cameraId = TestCameras.One;
    var cameraNode = Guid.Parse(CameraObjectId.For(cameraId));
    var input = Guid.Parse(DeviceObjectId.For(
      DeviceObjectId.CameraSource, cameraId, DeviceObjectId.DigitalInputPart, "1"));
    var relay = Guid.Parse(DeviceObjectId.For(
      DeviceObjectId.CameraSource, cameraId, DeviceObjectId.RelayPart, "1"));
    var nodes = new[] { cameraNode, input, relay };

    test.Db.Markers.AddRange(
      new DBMarker { id = cameraNode, name = "Camera" },
      new DBMarker { id = input, parent_id = cameraNode, name = "Input 1" },
      new DBMarker { id = relay, parent_id = cameraNode, name = "Relay 1" });
    await test.Db.SaveChangesAsync();

    test.Db.DeviceNodes.Add(TestCameras.DeviceNode(cameraId, cameraNode));
    test.Db.DeviceNodes.Add(new DBDeviceNode
    {
      object_id = input, source_kind = DeviceObjectId.CameraSource, source_id = cameraId,
      part_kind = DeviceObjectId.DigitalInputPart, part_token = "1",
    });
    test.Db.DeviceNodes.Add(new DBDeviceNode
    {
      object_id = relay, source_kind = DeviceObjectId.CameraSource, source_id = cameraId,
      part_kind = DeviceObjectId.RelayPart, part_token = "1",
    });
    // The registry rows follow their objects, and then the states and alarms: EF does not know
    // any of these point at objects, and would otherwise insert a child before its parent.
    await test.Db.SaveChangesAsync();

    foreach (var id in nodes)
    {
      test.Db.ObjectStates.Add(new DBObjectState
      {
        id = id,
        timestamp = DateTime.UtcNow,
        states = [new DBObjectStateValue { object_id = id, state = "SENSOR_OPEN" }],
      });
      test.Db.AlarmStates.Add(new DBAlarmState { id = id, alarm = true });
      test.Db.Events.Add(new DBEvent
      {
        id = Guid.NewGuid(), object_id = id, event_name = "sensor", timestamp = DateTime.UtcNow,
      });
    }
    await test.Db.SaveChangesAsync();

    var cameraNodes = new DeviceNodeService(
      test.Db, test.Map, test.Updates, NullLogger<DeviceNodeService>.Instance);
    await cameraNodes.RemoveCameraAsync(cameraId, CancellationToken.None);

    Assert.Empty(await test.Db.Markers.Where(m => nodes.Contains(m.id)).ToListAsync());
    Assert.Empty(await test.Db.DeviceNodes.Where(n => n.source_id == cameraId).ToListAsync());
    Assert.Empty(await test.Db.ObjectStates.Where(s => nodes.Contains(s.id)).ToListAsync());
    Assert.Empty(await test.Db.AlarmStates.Where(s => nodes.Contains(s.id)).ToListAsync());
    Assert.Empty(await test.Db.Events.Where(e => e.object_id != null && nodes.Contains(e.object_id)).ToListAsync());
  }
}
