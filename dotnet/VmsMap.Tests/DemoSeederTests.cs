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
    // way the real one does, and the names an administrator may rename them to.
    private readonly Dictionary<string, string> _known;

    public TestCatalog(params string[] known) =>
      _known = known.ToDictionary(DemoIds.Camera, id => CameraNamePrefix + id, StringComparer.Ordinal);

    /// A camera the installation provisions after the demo was installed once.
    public void Add(string cameraName) =>
      _known.TryAdd(DemoIds.Camera(cameraName), CameraNamePrefix + DemoIds.Camera(cameraName));

    /// The administrator renames the camera, as VmsCfg's own form would.
    public void Rename(string cameraName, string name) => _known[DemoIds.Camera(cameraName)] = name;

    public Task<CameraInfo?> GetAsync(string cameraId, CancellationToken ct) =>
      Task.FromResult(_known.TryGetValue(cameraId, out var name) ? new CameraInfo(cameraId, name, CatalogServerId) : null);

    public Task<List<CameraInfo>> ListAsync(string? serverId, CancellationToken ct) =>
      Task.FromResult(_known.Select(pair => new CameraInfo(pair.Key, pair.Value, CatalogServerId)).ToList());
  }

  private sealed class TestNotifier : IMapConfigNotifier
  {
    public Task PublishAsync(
      IReadOnlyCollection<string> objectIds, IReadOnlyCollection<string> cameraIds, CancellationToken ct) =>
      Task.CompletedTask;
  }

  private sealed record Services(
    VmsMapDbContext Db, MapDb Map, DemoSeeder Seeder, TestCatalog Catalog,
    TestCameraState CameraState, TestWiring.TestCfgArmingWriter Cfg);

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
    var updates = new MapUpdateService(db, map, tracks, states);
    var catalog = new TestCatalog(cameras);
    var cameraState = new TestCameraState();
    var cfg = new TestWiring.TestCfgArmingWriter(cameraState);
    var arming = TestWiring.CameraArming(db, map, cameraState, cfg);
    var spatial = new SpatialDb(db, map, updates, geo, catalog, new TestNotifier(), arming);

    var seeder = new DemoSeeder(
      db, updates, new DiagramService(db), new DiagramTypeService(db), levels, states,
      new RightService(db), geo, new FileService(db), new VisualService(db), spatial, catalog,
      TestWiring.DeviceNodes(db, map, updates), TestWiring.ObjectSettings(db),
      TestWiring.DeviceDiagrams(db, map, updates, geo));

    return new Services(db, map, seeder, catalog, cameraState, cfg);
  }

  // The fixtures these tests share.
  private const string Key = "umd-datacenter";
  private const string CameraNamePrefix = "Camera ";
  private const string CatalogServerId = "srv";
  private const string MapSpaceKey = "space/map";
  private const string RoomSpaceKey = "space/room";
  private const string MapSpaceName = "demo map";
  private const string RackTypeKey = "type/rack";
  private const string SlotKey = "slot-1";
  private const string RackKey = "object/rack";
  private const string SwitchKey = "object/switch";
  private const string PortKey = "object/port";
  private const string MasterCamera = "sim-master";
  private const string MissingCamera = "sim-missing";
  private const string PtzCamera = "sim-ptz";
  private const string MovedMasterCamera = "sim-master2";
  private const string RenamedCamera = "cam1";
  private const string SensorToken = "input";
  private const string DigitalInputPart = "digital_input";
  private const string RelayPart = "relay";
  private const string ButtonToken = "button";
  private const string ArmedMode = "armed";
  private const string ClosedTrigger = "closed";
  private const string FirstLandmarkKey = "landmark/l1";
  private const string SecondLandmarkKey = "landmark/l2";
  private const string FirstLandmarkName = "l1 red";
  private const string PanelFileKey = "file/panel";
  private const string PartButtonKey = "object/part-button";
  private const string PartButtonName = "relay4 rep1";
  private const string BoardTypeKey = "type/board";
  private const string BoardKey = "object/board";
  private const string BoardName = "board";
  private const string BoardLeft = "left";
  private const string PanelTypeKey = "type/panel";
  private const string PanelTypeName = "panel";
  private const string ShownKey = "object/cam-panel-left";

  private static DemoWorld World(
    IReadOnlyList<DemoNode>? nodes = null,
    IReadOnlyList<DemoPlacement>? placements = null,
    IReadOnlyList<DemoPartReplica>? partReplicas = null,
    IReadOnlyList<DemoDeviceSettings>? devices = null,
    IReadOnlyList<DemoLandmark>? landmarks = null,
    IReadOnlyList<DemoGroundMark>? groundMarks = null,
    IReadOnlyList<DemoPtzObservation>? ptzObservations = null,
    IReadOnlyList<DemoFile>? files = null,
    IReadOnlyList<DemoDeviceDiagram>? deviceDiagrams = null,
    IReadOnlyList<DemoDeviceRepresentation>? deviceRepresentations = null,
    IReadOnlyList<DemoDiagramType>? types = null) => new(
      Key,
      1,
      files ?? [],
      types ??
      [new DemoDiagramType(RackTypeKey, "Rack 42U", null,
        [new DemoRegion(SlotKey, new DemoRect(0.1, 0.2, 0.8, 0.1))])],
      [
        new DemoSpace(MapSpaceKey, MapSpaceName, null, SpaceKind.Map,
          39.0, -77.0, 39.1, -76.9),
        new DemoSpace(RoomSpaceKey, "room 101", MapSpaceKey, SpaceKind.Plan,
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
      partReplicas ?? [],
      devices ?? [],
      landmarks ?? [],
      groundMarks ?? [],
      ptzObservations ?? [],
      deviceDiagrams ?? [],
      deviceRepresentations ?? [],
      [new DemoState(PortKey, "DISCONNECTED")],
      [new DemoRight(MapSpaceKey, "user", RightActions.View)]);

  private static List<DemoNode> DefaultNodes() =>
  [
    new DemoNode(RackKey, "rack a-01", RoomSpaceKey, RackTypeKey,
      Canvas: new DemoRect(0, 0, 400, 800), At: new DemoPoint(39.015, -76.985)),
    new DemoNode(SwitchKey, "switch a-01", RackKey, RackTypeKey,
      RegionKey: SlotKey, Canvas: new DemoRect(0, 0, 300, 200)),
    new DemoNode(PortKey, "port 1", SwitchKey, RackTypeKey, RegionKey: SlotKey),
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
    var user = await UserNode(s.Map, "the administrator's note");

    await s.Seeder.InstallAsync(World(), CancellationToken.None);
    Assert.NotNull(await s.Db.Markers.FirstOrDefaultAsync(m => m.id == Guid.Parse(user.id!)));

    var report = await s.Seeder.RemoveAsync(Key, CancellationToken.None);

    Assert.True(report.Removed > 0);
    Assert.NotNull(await s.Db.Markers.FirstOrDefaultAsync(m => m.id == Guid.Parse(user.id!)));

    // The in-memory provider has no cascades, so the side rows of a removed node stay behind
    // there; what the demo owns is the tree nodes, and those are gone.
    var demoIds = new[] { RackKey, SwitchKey, PortKey }
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
    var kept = DemoIds.Id(SwitchKey);
    var dropped = DemoIds.Id(PortKey);

    var smaller = World(DefaultNodes().Where(node => node.Key != PortKey).ToList());
    var report = await s.Seeder.InstallAsync(smaller, CancellationToken.None);

    Assert.Equal(1, report.Removed);
    Assert.NotNull(await s.Db.Markers.FirstOrDefaultAsync(m => m.id == Guid.Parse(kept)));
    Assert.Null(await s.Db.Markers.FirstOrDefaultAsync(m => m.id == Guid.Parse(dropped)));
    Assert.Equal(0, report.Created);
  }

  [Fact]
  public async Task ACameraTheDeploymentLacksIsSkippedAndTheWorldStillInstalls()
  {
    var s = Create(MasterCamera);
    var world = World(placements:
    [
      new DemoPlacement(MasterCamera, RoomSpaceKey, 39.015, -76.985),
      new DemoPlacement(MissingCamera, RoomSpaceKey, 39.016, -76.984),
    ]);

    var report = await s.Seeder.InstallAsync(world, CancellationToken.None);

    Assert.Equal([MissingCamera], report.SkippedCameras);
    Assert.True((await s.Seeder.GetStatusAsync(Key, CancellationToken.None)).Installed);
    Assert.Equal(DemoIds.Camera(MasterCamera), (await s.Db.CameraPlacements.SingleAsync()).camera_id);
  }

  [Fact]
  public async Task ACameraArrivingLaterIsPlacedByAReinstall()
  {
    var s = Create();
    var world = World(placements: [new DemoPlacement(MasterCamera, RoomSpaceKey, 39.015, -76.985)]);

    var without = await s.Seeder.InstallAsync(world, CancellationToken.None);
    Assert.Single(without.SkippedCameras);
    Assert.Empty(await s.Db.CameraPlacements.ToListAsync());

    // The installation provisions the camera; the same world now places it.
    s.Catalog.Add(MasterCamera);
    var placed = await s.Seeder.InstallAsync(world, CancellationToken.None);

    Assert.Empty(placed.SkippedCameras);
    Assert.Single(await s.Db.CameraPlacements.ToListAsync());
  }

  [Fact]
  public async Task TheInstallLeavesTheFlagMatchingTheCommittedZone()
  {
    var s = Create(MasterCamera);
    var world = World(placements: [new DemoPlacement(MasterCamera, RoomSpaceKey, 39.015, -76.985)]);

    // Every install derives the flags of the cameras it drew: the room is off guard, so a camera
    // an earlier world left armed comes off it without waiting for another trigger.
    s.CameraState.Armed = true;
    await s.Seeder.InstallAsync(world, CancellationToken.None);

    Assert.False(s.CameraState.Armed);
    Assert.Contains((DemoIds.Camera(MasterCamera), false), s.Cfg.CameraFlags);
  }

  [Fact]
  public async Task TheWorldInstallsItsDevicesBehaviourOnThePartsNode()
  {
    // The map owns a logical object's settings, so the world writes them — not VmsCfg, whose half
    // of the demo keeps the part's name only.
    var s = Create(MasterCamera);
    var world = World(
      placements: [new DemoPlacement(MasterCamera, RoomSpaceKey, 39.015, -76.985)],
      devices:
      [
        new DemoDeviceSettings(MasterCamera, DigitalInputPart, SensorToken, ArmedMode, ClosedTrigger,
          AlarmEnabled: true),
      ]);

    await s.Seeder.InstallAsync(world, CancellationToken.None);

    var partId = Guid.Parse(DeviceObjectId.For(
      DeviceObjectId.CameraSource, DemoIds.Camera(MasterCamera), DigitalInputPart, SensorToken));
    var row = Assert.Single(await s.Db.DeviceObjectSettings.ToListAsync());
    Assert.Equal(partId, row.object_id);
    Assert.Equal(ArmedMode, row.arm_mode);
    Assert.Equal(ClosedTrigger, row.trigger_on);
    Assert.True(row.alarm_enabled);
  }

  [Fact]
  public async Task APartReplicaIsANodeOfThePartAndLeavesWithTheWorld()
  {
    // A part placed on a canvas is a replica of the part's canonical node — the row an
    // administrator's placement writes — with a point of its own and no camera placement. The
    // second install updates that row instead of adding another, and removing the world takes it.
    var s = Create(MasterCamera);
    var world = World(
      placements: [new DemoPlacement(MasterCamera, RoomSpaceKey, 39.015, -76.985)],
      partReplicas:
      [
        new DemoPartReplica(PartButtonKey, MasterCamera, RelayPart, ButtonToken, MapSpaceKey,
          PartButtonName, 39.05, -76.95, "#26a69a"),
      ]);

    var first = await s.Seeder.InstallAsync(world, CancellationToken.None);
    Assert.Empty(first.SkippedCameras);

    var partId = Guid.Parse(DeviceObjectId.For(
      DeviceObjectId.CameraSource, DemoIds.Camera(MasterCamera), RelayPart, ButtonToken));
    var picture = await s.Db.Markers.AsNoTracking().SingleAsync(m => m.owner_id == partId);
    var mapSpace = await s.Db.Markers.AsNoTracking().SingleAsync(m => m.name == MapSpaceName);
    Assert.Equal(DemoIds.For(PartButtonKey), picture.id);
    Assert.Equal(mapSpace.id, picture.parent_id);
    Assert.Equal(PartButtonName, picture.name);
    Assert.Empty(await s.Db.CameraPlacements.AsNoTracking()
      .Where(placement => placement.object_id == picture.id).ToListAsync());

    await s.Seeder.InstallAsync(world, CancellationToken.None);
    var again = await s.Db.Markers.AsNoTracking().SingleAsync(m => m.owner_id == partId);
    Assert.Equal(picture.id, again.id);

    await s.Seeder.RemoveAsync(Key, CancellationToken.None);
    Assert.Empty(await s.Db.Markers.AsNoTracking().Where(m => m.owner_id == partId).ToListAsync());
  }

  [Fact]
  public async Task ADeviceDiagramIsShownOnAnotherCanvasAsTheSameDiagram()
  {
    // The world shows the master's panel on a board of its own: the copy is a representation of the
    // canonical diagram kept under the camera's picture, placed through the service an
    // administrator's click reaches. A second install keeps the copy it finds, and a removal takes
    // both nodes with the world.
    var s = Create(MasterCamera);
    var world = World(
      placements: [new DemoPlacement(MasterCamera, RoomSpaceKey, 39.015, -76.985)],
      files: [new DemoFile(PanelFileKey, "<svg/>"u8.ToArray(), "image/svg+xml")],
      deviceDiagrams:
      [
        new DemoDeviceDiagram("object/panel", MasterCamera, PanelTypeKey, PanelFileKey, 44, 28),
      ],
      deviceRepresentations:
      [
        new DemoDeviceRepresentation(ShownKey, MasterCamera, PanelTypeKey, BoardKey, BoardLeft),
      ],
      types:
      [
        new DemoDiagramType(RackTypeKey, "Rack 42U", null,
          [new DemoRegion(SlotKey, new DemoRect(0.1, 0.2, 0.8, 0.1))]),
        new DemoDiagramType(BoardTypeKey, BoardName, null,
          [new DemoRegion(BoardLeft, new DemoRect(0.1, 0.2, 0.8, 0.8))]),
        new DemoDiagramType(PanelTypeKey, PanelTypeName, null, []),
      ],
      nodes:
      [
        .. DefaultNodes(),
        new DemoNode(BoardKey, BoardName, RoomSpaceKey, BoardTypeKey,
          Canvas: new DemoRect(0, 0, 400, 300)),
      ]);

    await s.Seeder.InstallAsync(world, CancellationToken.None);

    var boardId = DemoIds.For(BoardKey);
    var canonical = Guid.Parse(CameraObjectId.For(DemoIds.Camera(MasterCamera)));
    var picture = await s.Db.Markers.AsNoTracking().SingleAsync(m => m.owner_id == canonical);
    var canon = await s.Db.Markers.AsNoTracking().SingleAsync(m => m.parent_id == picture.id);
    var shown = await s.Db.Markers.AsNoTracking().SingleAsync(m => m.parent_id == boardId);

    // The copy is the service's node, with the id the service gave it — the registry is what
    // remembers it, so a second install and a removal find the same one.
    Assert.Equal(canon.id, shown.owner_id);
    // The copy carries its place and nothing else: the paper and the type are the canon's.
    var place = await s.Db.Diagrams.AsNoTracking().SingleAsync(d => d.id == shown.id);
    Assert.Equal(BoardLeft, place.region_id);
    Assert.Null(place.dgr_type);
    Assert.Null(place.geometry);

    var again = await s.Seeder.InstallAsync(world, CancellationToken.None);
    Assert.Equal(0, again.Created);
    Assert.Equal(shown.id, (await s.Db.Markers.AsNoTracking()
      .SingleAsync(m => m.parent_id == boardId)).id);

    await s.Seeder.RemoveAsync(Key, CancellationToken.None);
    Assert.Empty(await s.Db.Markers.AsNoTracking()
      .Where(m => m.id == canon.id || m.id == shown.id || m.id == boardId).ToListAsync());
  }

  [Fact]
  public async Task AWorldThatRenamesItsCameraRenamesItsPicture()
  {
    // A picture already on the canvas is not renamed by a placement write. The world's own picture
    // is the world's row, so the seeder keeps its name in step with the camera: a version of the
    // demo that renames its cameras must not leave yesterday's names on the map.
    var s = Create(MasterCamera);
    var world = World(placements: [new DemoPlacement(MasterCamera, RoomSpaceKey, 39.015, -76.985)]);
    await s.Seeder.InstallAsync(world, CancellationToken.None);

    var canonical = Guid.Parse(CameraObjectId.For(DemoIds.Camera(MasterCamera)));
    var before = await s.Db.Markers.AsNoTracking().SingleAsync(m => m.owner_id == canonical);
    Assert.Equal("Camera sim-master rep1", before.name);

    s.Catalog.Rename(MasterCamera, RenamedCamera);
    await s.Seeder.InstallAsync(world, CancellationToken.None);

    var after = await s.Db.Markers.AsNoTracking().SingleAsync(m => m.owner_id == canonical);
    Assert.Equal(before.id, after.id);
    Assert.Equal("cam1 rep1", after.name);
  }

  [Fact]
  public async Task AWorldThatRenamesItsCameraRenamesTheDiagramUnderIt()
  {
    // The device's diagram is named `<camera> / <type>` once, when the service makes the node, and
    // an attach afterwards keeps the name it finds. The camera is the world's to name, so a version
    // of the world that renames its cameras must not leave yesterday's name on the panel under the
    // picture — and the rename must not orphan the node it renames.
    var s = Create(MasterCamera);
    var world = World(
      placements: [new DemoPlacement(MasterCamera, RoomSpaceKey, 39.015, -76.985)],
      files: [new DemoFile(PanelFileKey, "<svg/>"u8.ToArray(), "image/svg+xml")],
      deviceDiagrams:
      [
        new DemoDeviceDiagram("object/panel", MasterCamera, RackTypeKey, PanelFileKey, 44, 28),
      ]);

    await s.Seeder.InstallAsync(world, CancellationToken.None);

    var canonical = Guid.Parse(CameraObjectId.For(DemoIds.Camera(MasterCamera)));
    var picture = await s.Db.Markers.AsNoTracking().SingleAsync(m => m.owner_id == canonical);
    var before = await s.Db.Markers.AsNoTracking().SingleAsync(m => m.parent_id == picture.id);
    Assert.Equal("Camera sim-master / Rack 42U", before.name);

    s.Catalog.Rename(MasterCamera, RenamedCamera);
    await s.Seeder.InstallAsync(world, CancellationToken.None);

    var after = await s.Db.Markers.AsNoTracking().SingleAsync(m => m.id == before.id);
    Assert.Equal("cam1 / Rack 42U", after.name);
    Assert.Equal(picture.id, after.parent_id);
  }

  [Fact]
  public async Task ADeviceOfACameraTheDeploymentLacksIsSkipped()
  {
    var s = Create();
    var world = World(
      placements: [new DemoPlacement(MasterCamera, RoomSpaceKey, 39.015, -76.985)],
      devices: [new DemoDeviceSettings(MasterCamera, DigitalInputPart, SensorToken, ArmedMode)]);

    var report = await s.Seeder.InstallAsync(world, CancellationToken.None);

    Assert.Equal([MasterCamera], report.SkippedCameras);
    Assert.Empty(await s.Db.DeviceObjectSettings.ToListAsync());
  }

  [Fact]
  public async Task ACameraTheWorldMovedToAnotherSpaceKeepsOnePlacement()
  {
    // The second pair moved out of the building onto the map: the camera is one picture in one
    // space, not one in each, and the old one must not survive the reinstall unreachable from the
    // registry.
    var s = Create(MovedMasterCamera);
    var inTheRoom = World(placements: [new DemoPlacement(MovedMasterCamera, RoomSpaceKey, 39.015, -76.985)]);
    await s.Seeder.InstallAsync(inTheRoom, CancellationToken.None);
    Assert.Single(await s.Db.CameraPlacements.ToListAsync());

    var onTheMap = World(placements: [new DemoPlacement(MovedMasterCamera, MapSpaceKey, 39.05, -76.95)]);
    var report = await s.Seeder.InstallAsync(onTheMap, CancellationToken.None);

    var placement = Assert.Single(await s.Db.CameraPlacements.ToListAsync());
    var node = await s.Db.Markers.AsNoTracking().SingleAsync(m => m.id == placement.object_id);
    // A space's id is the server's, so the map is found by the name the world gives it.
    var mapSpace = await s.Db.Markers.AsNoTracking().SingleAsync(m => m.name == MapSpaceName);
    Assert.Equal(mapSpace.id, node.parent_id);
    Assert.Empty(report.SkippedCameras);
  }

  [Fact]
  public async Task LandmarksAndTheirCalibrationsAreWrittenAndRemovedWithTheWorld()
  {
    var s = Create(MasterCamera, PtzCamera);
    var world = World(
      placements:
      [
        new DemoPlacement(MasterCamera, RoomSpaceKey, 39.015, -76.985),
        new DemoPlacement(PtzCamera, RoomSpaceKey, 39.016, -76.985),
      ],
      landmarks:
      [
        new DemoLandmark(FirstLandmarkKey, FirstLandmarkName, RoomSpaceKey, 39.017, -76.984),
        new DemoLandmark(SecondLandmarkKey, "l2 blue", RoomSpaceKey, 39.018, -76.983),
      ],
      groundMarks:
      [
        new DemoGroundMark(MasterCamera, FirstLandmarkKey, 0.4, 0.7),
        new DemoGroundMark(MasterCamera, SecondLandmarkKey, 0.6, 0.7),
      ],
      ptzObservations:
      [
        new DemoPtzObservation(PtzCamera, FirstLandmarkKey, 0.1, 0.3, 0.2),
        new DemoPtzObservation(PtzCamera, SecondLandmarkKey, -0.1, 0.4),
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
    var s = Create(MasterCamera);
    var world = World(
      placements: [new DemoPlacement(MasterCamera, RoomSpaceKey, 39.015, -76.985)],
      landmarks: [new DemoLandmark(FirstLandmarkKey, FirstLandmarkName, RoomSpaceKey, 39.017, -76.984)],
      groundMarks:
      [
        new DemoGroundMark(MasterCamera, FirstLandmarkKey, 0.4, 0.7),
        new DemoGroundMark(MissingCamera, FirstLandmarkKey, 0.5, 0.7),
      ]);

    var report = await s.Seeder.InstallAsync(world, CancellationToken.None);

    Assert.Equal([MissingCamera], report.SkippedCameras);
    var mark = Assert.Single(await s.Db.GroundObservations.ToListAsync());
    Assert.Equal(DemoIds.Camera(MasterCamera), mark.camera_id);
  }

}
