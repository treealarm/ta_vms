using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Vms.Map;
using VmsMap.Adapter;
using VmsMap.Clients;
using VmsMap.Db;
using VmsMap.Demo;
using VmsMap.Domain;
using VmsMap.PubSub;

namespace VmsMap.Tests;

// The demo seeder against the in-memory provider: what a second install does to the rows of the
// first, what a removal takes and what it leaves. The world here is a miniature of the real one —
// a map, a room, a rack, a switch and a port — with no pictures, because assets are the content of
// the world, not the machinery that writes it.
public class DemoSeederTests
{
  private sealed class TestCatalog : ICameraCatalog
  {
    // The demo world names its cameras; VmsCfg keys them by id. The fake catalogue holds ids, the
    // way the real one does.
    private readonly HashSet<string> _known;

    public TestCatalog(params string[] known) =>
      _known = new HashSet<string>(known.Select(DemoIds.Camera), StringComparer.Ordinal);

    /// A camera the installation provisions after the demo was installed once.
    public void Add(string cameraName) => _known.Add(DemoIds.Camera(cameraName));

    public Task<CameraInfo?> GetAsync(string cameraId, CancellationToken ct) =>
      Task.FromResult(_known.Contains(cameraId)
        ? new CameraInfo(cameraId, "Camera " + cameraId, "srv")
        : null);

    public Task<List<CameraInfo>> ListAsync(string? serverId, CancellationToken ct) =>
      Task.FromResult(_known.Select(id => new CameraInfo(id, "Camera " + id, "srv")).ToList());
  }

  private sealed class TestNotifier : IMapConfigNotifier
  {
    public Task PublishAsync(
      IReadOnlyCollection<string> objectIds, IReadOnlyCollection<string> cameraIds, CancellationToken ct) =>
      Task.CompletedTask;
  }

  private sealed record Services(VmsMapDbContext Db, MapDb Map, DemoSeeder Seeder, TestCatalog Catalog);

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
    var tracks = new TracksUpdateService(map, new TrackService(db, levels), geo);
    var updates = new MapUpdateService(map, tracks, states);
    var catalog = new TestCatalog(cameras);
    var spatial = new SpatialDb(db, map, updates, geo, catalog, new TestNotifier());

    var seeder = new DemoSeeder(
      db, updates, new DiagramService(db), new DiagramTypeService(db), levels, states,
      new RightService(db), geo, new FileService(db), new VisualService(db), spatial, catalog,
      TestWiring.DeviceNodes(db, map, updates), TestWiring.DeviceDiagrams(db, map, updates, geo));

    return new Services(db, map, seeder, catalog);
  }

  private const string Key = "umd-datacenter";

  private static DemoWorld World(
    IReadOnlyList<DemoNode>? nodes = null,
    IReadOnlyList<DemoPlacement>? placements = null,
    IReadOnlyList<DemoLandmark>? landmarks = null,
    IReadOnlyList<DemoGroundMark>? groundMarks = null,
    IReadOnlyList<DemoPtzObservation>? ptzObservations = null) => new(
      Key,
      1,
      [],
      [new DemoDiagramType("type/rack", "Rack 42U", null,
        [new DemoRegion("slot-1", new DemoRect(0.1, 0.2, 0.8, 0.1))])],
      [
        new DemoSpace("space/map", "Demo map", null, SpaceKind.Map,
          39.0, -77.0, 39.1, -76.9),
        new DemoSpace("space/room", "Room 101", "space/map", SpaceKind.Plan,
          39.01, -76.99, 39.02, -76.98,
          Footprint:
          [
            new DemoPoint(39.011, -76.989),
            new DemoPoint(39.011, -76.981),
            new DemoPoint(39.019, -76.981),
          ]),
      ],
      nodes ?? DefaultNodes(),
      placements ?? [],
      landmarks ?? [],
      groundMarks ?? [],
      ptzObservations ?? [],
      [],
      [new DemoState("object/port", "DISCONNECTED")],
      [new DemoRight("space/map", "user", RightActions.View)]);

  private static List<DemoNode> DefaultNodes() =>
  [
    new DemoNode("object/rack", "Rack A-01", "space/room", "type/rack",
      Canvas: new DemoRect(0, 0, 400, 800), At: new DemoPoint(39.015, -76.985)),
    new DemoNode("object/switch", "Switch A-01", "object/rack", "type/rack",
      RegionKey: "slot-1", Canvas: new DemoRect(0, 0, 300, 200)),
    new DemoNode("object/port", "Port 1", "object/switch", "type/rack", RegionKey: "slot-1"),
  ];

  private static async Task<BaseMarkerDTO> UserNode(MapDb map, string name)
  {
    var node = new BaseMarkerDTO { name = name };
    await map.UpdateHierarchyAsync([node]);
    return node;
  }

  [Fact]
  public async Task InstallTwiceKeepsTheSameRowsAndIds()
  {
    var s = Create();

    var first = await s.Seeder.InstallAsync(World(), CancellationToken.None);
    var statusBefore = await s.Seeder.GetStatusAsync(Key, CancellationToken.None);
    var markersBefore = await s.Db.Markers.OrderBy(m => m.id).Select(m => m.id).ToListAsync();
    var diagramsBefore = await s.Db.Diagrams.CountAsync();

    var second = await s.Seeder.InstallAsync(World(), CancellationToken.None);
    var statusAfter = await s.Seeder.GetStatusAsync(Key, CancellationToken.None);
    var markersAfter = await s.Db.Markers.OrderBy(m => m.id).Select(m => m.id).ToListAsync();

    Assert.True(first.Created > 0);
    Assert.Equal(0, second.Created);
    Assert.Equal(0, second.Removed);
    Assert.Equal(markersBefore, markersAfter);
    Assert.Equal(diagramsBefore, await s.Db.Diagrams.CountAsync());
    Assert.Equal(statusBefore.Entities, statusAfter.Entities);
  }

  [Fact]
  public async Task RemovalTakesTheDemoAndLeavesTheUsersOwnNode()
  {
    var s = Create();
    var user = await UserNode(s.Map, "The administrator's note");

    await s.Seeder.InstallAsync(World(), CancellationToken.None);
    Assert.NotNull(await s.Db.Markers.FirstOrDefaultAsync(m => m.id == Guid.Parse(user.id!)));

    var report = await s.Seeder.RemoveAsync(Key, CancellationToken.None);

    Assert.True(report.Removed > 0);
    Assert.NotNull(await s.Db.Markers.FirstOrDefaultAsync(m => m.id == Guid.Parse(user.id!)));

    // The in-memory provider has no cascades, so the side rows of a removed node stay behind
    // there; what the demo owns is the tree nodes, and those are gone.
    var demoIds = new[] { "object/rack", "object/switch", "object/port" }
      .Select(DemoIds.For)
      .ToList();
    Assert.Empty(await s.Db.Markers.Where(m => demoIds.Contains(m.id)).ToListAsync());
    Assert.Empty(await s.Db.Markers.Where(m => m.id != Guid.Parse(user.id!)).ToListAsync());
    Assert.Empty(await s.Db.DemoObjects.ToListAsync());
    Assert.False((await s.Seeder.GetStatusAsync(Key, CancellationToken.None)).Installed);
  }

  [Fact]
  public async Task AWorldThatDroppedANodeRemovesOnlyThatNode()
  {
    var s = Create();
    await s.Seeder.InstallAsync(World(), CancellationToken.None);
    var kept = DemoIds.Id("object/switch");
    var dropped = DemoIds.Id("object/port");

    var smaller = World(DefaultNodes().Where(node => node.Key != "object/port").ToList());
    var report = await s.Seeder.InstallAsync(smaller, CancellationToken.None);

    Assert.Equal(1, report.Removed);
    Assert.NotNull(await s.Db.Markers.FirstOrDefaultAsync(m => m.id == Guid.Parse(kept)));
    Assert.Null(await s.Db.Markers.FirstOrDefaultAsync(m => m.id == Guid.Parse(dropped)));
    Assert.Equal(0, report.Created);
  }

  [Fact]
  public async Task ACameraTheDeploymentLacksIsSkippedAndTheWorldStillInstalls()
  {
    var s = Create("sim-master");
    var world = World(placements:
    [
      new DemoPlacement("sim-master", "space/room", 39.015, -76.985),
      new DemoPlacement("sim-missing", "space/room", 39.016, -76.984),
    ]);

    var report = await s.Seeder.InstallAsync(world, CancellationToken.None);

    Assert.Equal(["sim-missing"], report.SkippedCameras);
    Assert.True((await s.Seeder.GetStatusAsync(Key, CancellationToken.None)).Installed);
    Assert.Equal(DemoIds.Camera("sim-master"), (await s.Db.CameraPlacements.SingleAsync()).camera_id);
  }

  [Fact]
  public async Task ACameraArrivingLaterIsPlacedByAReinstall()
  {
    var s = Create();
    var world = World(placements: [new DemoPlacement("sim-master", "space/room", 39.015, -76.985)]);

    var without = await s.Seeder.InstallAsync(world, CancellationToken.None);
    Assert.Single(without.SkippedCameras);
    Assert.Empty(await s.Db.CameraPlacements.ToListAsync());

    // The installation provisions the camera; the same world now places it.
    s.Catalog.Add("sim-master");
    var placed = await s.Seeder.InstallAsync(world, CancellationToken.None);

    Assert.Empty(placed.SkippedCameras);
    Assert.Single(await s.Db.CameraPlacements.ToListAsync());
  }

  [Fact]
  public async Task ACameraTheWorldMovedToAnotherSpaceKeepsOnePlacement()
  {
    // The second pair moved out of the building onto the map: the camera is one picture in one
    // space, not one in each, and the old one must not survive the reinstall unreachable from the
    // registry.
    var s = Create("sim-master2");
    var inTheRoom = World(placements: [new DemoPlacement("sim-master2", "space/room", 39.015, -76.985)]);
    await s.Seeder.InstallAsync(inTheRoom, CancellationToken.None);
    Assert.Single(await s.Db.CameraPlacements.ToListAsync());

    var onTheMap = World(placements: [new DemoPlacement("sim-master2", "space/map", 39.05, -76.95)]);
    var report = await s.Seeder.InstallAsync(onTheMap, CancellationToken.None);

    var placement = Assert.Single(await s.Db.CameraPlacements.ToListAsync());
    var node = await s.Db.Markers.AsNoTracking().SingleAsync(m => m.id == placement.object_id);
    // A space's id is the server's, so the map is found by the name the world gives it.
    var mapSpace = await s.Db.Markers.AsNoTracking().SingleAsync(m => m.name == "Demo map");
    Assert.Equal(mapSpace.id, node.parent_id);
    Assert.Empty(report.SkippedCameras);
  }

  [Fact]
  public async Task LandmarksAndTheirCalibrationsAreWrittenAndRemovedWithTheWorld()
  {
    var s = Create("sim-master", "sim-ptz");
    var world = World(
      placements:
      [
        new DemoPlacement("sim-master", "space/room", 39.015, -76.985),
        new DemoPlacement("sim-ptz", "space/room", 39.016, -76.985),
      ],
      landmarks:
      [
        new DemoLandmark("landmark/l1", "L1 red", "space/room", 39.017, -76.984),
        new DemoLandmark("landmark/l2", "L2 blue", "space/room", 39.018, -76.983),
      ],
      groundMarks:
      [
        new DemoGroundMark("sim-master", "landmark/l1", 0.4, 0.7),
        new DemoGroundMark("sim-master", "landmark/l2", 0.6, 0.7),
      ],
      ptzObservations:
      [
        new DemoPtzObservation("sim-ptz", "landmark/l1", 0.1, 0.3, 0.2),
        new DemoPtzObservation("sim-ptz", "landmark/l2", -0.1, 0.4),
      ]);

    var first = await s.Seeder.InstallAsync(world, CancellationToken.None);
    Assert.True(first.Created > 0);

    var landmarks = await s.Db.Landmarks.AsNoTracking().OrderBy(l => l.object_id).ToListAsync();
    Assert.Equal(2, landmarks.Count);
    Assert.Equal(2, await s.Db.GroundObservations.CountAsync());
    Assert.Equal(2, await s.Db.PtzGeoObservations.CountAsync());

    // A second install is a no-op on the ids: the registry remembers what the server assigned.
    var ids = landmarks.Select(l => l.object_id).ToList();
    var second = await s.Seeder.InstallAsync(world, CancellationToken.None);
    Assert.Equal(0, second.Created);
    Assert.Equal(ids, await s.Db.Landmarks.AsNoTracking().OrderBy(l => l.object_id)
      .Select(l => l.object_id).ToListAsync());

    // The marks belong to the landmarks and go with them.
    await s.Seeder.RemoveAsync(Key, CancellationToken.None);
    Assert.Empty(await s.Db.Landmarks.ToListAsync());
    Assert.Empty(await s.Db.GroundObservations.ToListAsync());
    Assert.Empty(await s.Db.PtzGeoObservations.ToListAsync());
  }

  [Fact]
  public async Task CalibrationIsSkippedForACameraTheDeploymentLacks()
  {
    var s = Create("sim-master");
    var world = World(
      placements: [new DemoPlacement("sim-master", "space/room", 39.015, -76.985)],
      landmarks: [new DemoLandmark("landmark/l1", "L1 red", "space/room", 39.017, -76.984)],
      groundMarks:
      [
        new DemoGroundMark("sim-master", "landmark/l1", 0.4, 0.7),
        new DemoGroundMark("sim-missing", "landmark/l1", 0.5, 0.7),
      ]);

    var report = await s.Seeder.InstallAsync(world, CancellationToken.None);

    Assert.Equal(["sim-missing"], report.SkippedCameras);
    var mark = Assert.Single(await s.Db.GroundObservations.ToListAsync());
    Assert.Equal(DemoIds.Camera("sim-master"), mark.camera_id);
  }

  private static DemoNode TelemetryReplica() =>
    new("object/telemetry", "Camera sim-vehicle", "space/room", "type/rack",
      Canvas: new DemoRect(0, 0, 300, 200), OwnerCameraId: "sim-vehicle");

  [Fact]
  public async Task AReplicaHangsOffTheCamerasCanonicalNodeAndTakesNoPlacement()
  {
    var s = Create("sim-vehicle");

    await s.Seeder.InstallAsync(World(nodes: [.. DefaultNodes(), TelemetryReplica()]), CancellationToken.None);

    var replicaId = Guid.Parse(DemoIds.Id("object/telemetry"));
    var replica = await s.Db.Markers.AsNoTracking().SingleAsync(m => m.id == replicaId);
    Assert.Equal(Guid.Parse(CameraObjectId.For(DemoIds.Camera("sim-vehicle"))), replica.owner_id);

    // The camera stands where its placement put it; the replica shows it from a diagram, which is
    // not a space, so there is nothing to place.
    Assert.Empty(await s.Db.CameraPlacements.ToListAsync());
  }

  [Fact]
  public async Task AReplicaIsSkippedWhileTheDeploymentLacksTheCamera()
  {
    var s = Create();

    var report = await s.Seeder.InstallAsync(
      World(nodes: [.. DefaultNodes(), TelemetryReplica()]), CancellationToken.None);

    Assert.Contains("sim-vehicle", report.SkippedCameras);
    Assert.Null(await s.Db.Markers.AsNoTracking()
      .FirstOrDefaultAsync(m => m.id == Guid.Parse(DemoIds.Id("object/telemetry"))));
  }
}
