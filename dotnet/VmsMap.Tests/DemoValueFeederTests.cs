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
using VmsMap.Demo;
using VmsMap.Domain;
using VmsMap.PubSub;

namespace VmsMap.Tests;

// The demo's own readings: the camera's position, speed, relay and sensor, written where the map
// answers a replica with its owner's values — and taken away with the world.
public class DemoValueFeederTests
{
  // What the world calls its cameras, and the ids VmsCfg keys them by.
  private static readonly string[] DemoCameras =
    ["sim-master", "sim-master2", "sim-ptz", "sim-ptz2", "sim-vehicle"];

  private static readonly string Vehicle = DemoIds.Camera("sim-vehicle");
  private static readonly string Master = DemoIds.Camera("sim-master");
  private static readonly string Ptz = DemoIds.Camera("sim-ptz");

  // About 1 m of latitude; a degree is 2πR/360.
  private const double OneMetreInLatitude = 1.0 / 111_319.5;

  private sealed class TestCatalog(params string[] cameras) : ICameraCatalog
  {
    private readonly List<string> _known = [.. cameras.Select(DemoIds.Camera)];

    public Task<CameraInfo?> GetAsync(string cameraId, CancellationToken ct) =>
      Task.FromResult(_known.Contains(cameraId)
        ? new CameraInfo(cameraId, "Camera " + cameraId, "srv")
        : null);

    public Task<List<CameraInfo>> ListAsync(string? serverId, CancellationToken ct) =>
      Task.FromResult(_known.Select(id => new CameraInfo(id, "Camera " + id, "srv")).ToList());
  }

  private sealed class TestGeo : ICameraGeoSource
  {
    public Dictionary<string, CameraGeoPoint> Points { get; } = new(StringComparer.Ordinal);

    public Task<CameraGeoPoint?> GetAsync(CameraInfo camera, CancellationToken ct) =>
      Task.FromResult(Points.TryGetValue(camera.CameraId, out var point)
        ? point
        : (CameraGeoPoint?)null);
  }

  private sealed class TestIo : ICameraDeviceIoSource
  {
    public Dictionary<string, bool?> Relays { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, bool?> Inputs { get; } = new(StringComparer.Ordinal);

    public Task<CameraDeviceState> GetRelayAsync(CameraInfo camera, CancellationToken ct) =>
      Task.FromResult(Relays.TryGetValue(camera.CameraId, out var live)
        ? new CameraDeviceState(true, live)
        : new CameraDeviceState(false, null));

    public Task<CameraDeviceState> GetInputAsync(CameraInfo camera, CancellationToken ct) =>
      Task.FromResult(Inputs.TryGetValue(camera.CameraId, out var live)
        ? new CameraDeviceState(true, live)
        : new CameraDeviceState(false, null));
  }

  private sealed class TestTime : TimeProvider
  {
    public DateTimeOffset Now { get; set; } = DateTimeOffset.UnixEpoch;

    public override DateTimeOffset GetUtcNow() => Now;
  }

  private sealed class TestNotifier : IMapConfigNotifier
  {
    public Task PublishAsync(
      IReadOnlyCollection<string> objectIds, IReadOnlyCollection<string> cameraIds, CancellationToken ct) =>
      Task.CompletedTask;
  }

  private sealed record Services(
    VmsMapDbContext Db, MapDb Map, DemoSeeder Seeder, DemoValueFeeder Feeder,
    TestGeo Geo, TestIo Io, TestTime Time);

  private static Services Create(params string[] cameras)
  {
    var options = new DbContextOptionsBuilder<VmsMapDbContext>()
      .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
      .Options;

    var db = new VmsMapDbContext(options);
    var map = new MapDb(db);
    var states = new StatesUpdateService(new StateService(db), map, db);
    var levels = new LevelService(db);
    var geo = new GeoService(db, levels);
    var updates = new MapUpdateService(map, new TracksUpdateService(map, new TrackService(db, levels), geo), states);
    var values = new ValuesService(db);
    var catalog = new TestCatalog(cameras.Length > 0 ? cameras : DemoCameras);
    var spatial = new SpatialDb(db, map, updates, geo, catalog, new TestNotifier());
    var cameraNodes = new CameraNodeService(db, map, updates, NullLogger<CameraNodeService>.Instance);

    var seeder = new DemoSeeder(
      db, updates, new DiagramService(db), new DiagramTypeService(db), levels, states,
      new RightService(db), geo, new FileService(db), new VisualService(db), spatial, catalog,
      cameraNodes);

    var geoSource = new TestGeo();
    var ioSource = new TestIo();
    var time = new TestTime();

    return new Services(
      db, map, seeder,
      new DemoValueFeeder(db, values, catalog, geoSource, ioSource, new DemoSpeedTracker(time)),
      geoSource, ioSource, time);
  }

  private static async Task<Dictionary<string, ValueDTO>> ValuesOf(VmsMapDbContext db, Guid owner)
  {
    var rows = await db.Values.AsNoTracking().Where(v => v.owner_id == owner).ToListAsync();
    return rows.ToDictionary(row => row.name, row => ValuesService.ConvertDB2DTO(row));
  }

  private static Guid ReplicaId() => Guid.Parse(DemoIds.Id("object/telemetry-sim-vehicle"));

  private static async Task<Guid> OwnerOfReplicaAsync(VmsMapDbContext db) =>
    (await db.Markers.AsNoTracking().SingleAsync(m => m.id == ReplicaId())).owner_id
      ?? throw new InvalidOperationException("The telemetry diagram is not a replica");

  [Fact]
  public async Task TheReplicaShowsTheOwnersReadings()
  {
    var s = Create();
    await s.Seeder.InstallAsync(DemoWorlds.UmdDataCenter(), CancellationToken.None);
    s.Geo.Points[Vehicle] = new CameraGeoPoint(38.9923, -76.9378);

    var written = await s.Feeder.RunOnceAsync(CancellationToken.None);

    var owner = await OwnerOfReplicaAsync(s.Db);
    Assert.Equal(Guid.Parse(CameraObjectId.For(Vehicle)), owner);

    var values = await ValuesOf(s.Db, owner);
    Assert.Equal(2, written);
    Assert.Equal("38.992300", values["lat"].value?.ToString());
    Assert.Equal("-76.937800", values["lon"].value?.ToString());

    // A diagram's canvas is not a space: the replica has no placement of its own.
    Assert.False(await s.Db.CameraPlacements.AnyAsync(p => p.object_id == ReplicaId()));
  }

  [Fact]
  public async Task TheSpeedComesFromTwoPlacesAndTheTimeBetweenThem()
  {
    var s = Create();
    await s.Seeder.InstallAsync(DemoWorlds.UmdDataCenter(), CancellationToken.None);

    s.Time.Now = DateTimeOffset.UnixEpoch;
    s.Geo.Points[Vehicle] = new CameraGeoPoint(38.9923, -76.9378);
    await s.Feeder.RunOnceAsync(CancellationToken.None);

    // Five metres north over five seconds: one metre per second, 3.6 km/h.
    s.Time.Now = DateTimeOffset.UnixEpoch.AddSeconds(5);
    s.Geo.Points[Vehicle] = new CameraGeoPoint(38.9923 + 5 * OneMetreInLatitude, -76.9378);
    await s.Feeder.RunOnceAsync(CancellationToken.None);

    var values = await ValuesOf(s.Db, await OwnerOfReplicaAsync(s.Db));
    Assert.Equal("3.6", values["speed"].value?.ToString());
  }

  [Fact]
  public async Task TheRelayAndTheSensorComeFromWhicheverCameraHasThem()
  {
    var s = Create();
    await s.Seeder.InstallAsync(DemoWorlds.UmdDataCenter(), CancellationToken.None);
    s.Geo.Points[Vehicle] = new CameraGeoPoint(38.9923, -76.9378);
    // The wires of the demo: sim-master's relay closes sim-ptz's input, so it reads 0.
    s.Io.Relays[Master] = true;
    s.Io.Inputs[Ptz] = false;

    await s.Feeder.RunOnceAsync(CancellationToken.None);

    var values = await ValuesOf(s.Db, await OwnerOfReplicaAsync(s.Db));
    Assert.Equal("on", values["relay"].value?.ToString());
    Assert.Equal("off", values["sensor"].value?.ToString());
  }

  [Fact]
  public async Task ADeviceWithoutAReportedStateIsNotWritten()
  {
    var s = Create();
    await s.Seeder.InstallAsync(DemoWorlds.UmdDataCenter(), CancellationToken.None);
    s.Geo.Points[Vehicle] = new CameraGeoPoint(38.9923, -76.9378);
    s.Io.Relays[Master] = null; // subscribed, nothing announced yet

    await s.Feeder.RunOnceAsync(CancellationToken.None);

    var values = await ValuesOf(s.Db, await OwnerOfReplicaAsync(s.Db));
    Assert.DoesNotContain("relay", values.Keys);
  }

  [Fact]
  public async Task ACameraThatReportsNoPlaceWritesNothing()
  {
    var s = Create();
    await s.Seeder.InstallAsync(DemoWorlds.UmdDataCenter(), CancellationToken.None);

    Assert.Equal(0, await s.Feeder.RunOnceAsync(CancellationToken.None));
    Assert.Empty(await s.Db.Values.ToListAsync());
  }

  [Fact]
  public async Task TheFeederLeavesAUsersDiagramAloneEvenWithTheTelemetryType()
  {
    var s = Create();
    await s.Seeder.InstallAsync(DemoWorlds.UmdDataCenter(), CancellationToken.None);
    s.Geo.Points[Vehicle] = new CameraGeoPoint(38.9923, -76.9378);

    var user = new BaseMarkerDTO { id = Guid.NewGuid().ToString(), name = "The administrator's panel" };
    await s.Map.UpdateHierarchyAsync([user]);
    await new DiagramService(s.Db).UpdateListAsync([new DiagramDTO
    {
      id = user.id,
      dgr_type = DemoIds.Id(DemoTelemetry.TypeKey),
      geometry = new DiagramCoordDTO { width = 300, height = 200 },
    }]);

    await s.Feeder.RunOnceAsync(CancellationToken.None);

    Assert.Empty(await s.Db.Values.Where(v => v.owner_id == Guid.Parse(user.id!)).ToListAsync());
  }

  [Fact]
  public async Task TheFeederWritesNothingWhileNoWorldIsInstalled()
  {
    var s = Create();

    Assert.Equal(0, await s.Feeder.RunOnceAsync(CancellationToken.None));
    Assert.Empty(await s.Db.Values.ToListAsync());
  }

  [Fact]
  public async Task RemovingTheWorldTakesItsReadingsWithIt()
  {
    var s = Create();
    await s.Seeder.InstallAsync(DemoWorlds.UmdDataCenter(), CancellationToken.None);
    s.Geo.Points[Vehicle] = new CameraGeoPoint(38.9923, -76.9378);
    await s.Feeder.RunOnceAsync(CancellationToken.None);
    Assert.NotEmpty(await s.Db.Values.ToListAsync());

    await s.Seeder.RemoveAsync(DemoWorlds.UmdDataCenterKey, CancellationToken.None);

    Assert.Empty(await s.Db.Values.ToListAsync());
    Assert.Equal(0, await s.Feeder.RunOnceAsync(CancellationToken.None));
  }
}
