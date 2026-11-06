using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Vms.Map;
using VmsMap.Adapter;
using VmsMap.Clients;
using VmsMap.Db;
using VmsMap.Demo;
using VmsMap.Domain;
using VmsMap.Grpc;
using VmsMap.PubSub;
using MapProto = Vms.Map;

namespace VmsMap.Tests;

// A canvas is the unit of drawing: everything whose figure is drawn in that space's coordinates,
// at whatever depth of the tree, and nothing from another canvas that happens to share the same
// numbers. The rectangle stays what it was -- an area of one canvas's coordinates.
public class CanvasQueryTests
{
  private sealed class TestCatalog : ICameraCatalog
  {
    public Task<CameraInfo?> GetAsync(string cameraId, CancellationToken ct) =>
      Task.FromResult<CameraInfo?>(new CameraInfo(cameraId, "Camera " + cameraId, "srv"));

    public Task<List<CameraInfo>> ListAsync(string? serverId, CancellationToken ct) =>
      Task.FromResult(new List<CameraInfo>());
  }

  private sealed class TestNotifier : IMapConfigNotifier
  {
    public Task PublishAsync(
      IReadOnlyCollection<string> objectIds, IReadOnlyCollection<string> cameraIds, CancellationToken ct) =>
      Task.CompletedTask;
  }

  private sealed class TestRightsNotifier : IRightsVersionNotifier
  {
    public Task BumpAsync(CancellationToken ct) => Task.CompletedTask;
  }

  private sealed record Services(
    VmsMapDbContext Db, MapDb Map, GeoService Geo, SpatialDb Spatial, MapServiceImpl Grpc);

  private static Services Create()
  {
    var options = new DbContextOptionsBuilder<VmsMapDbContext>()
      .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
      .Options;

    var db = new VmsMapDbContext(options);
    var map = new MapDb(db);
    var stateService = new StateService(db);
    var states = new StatesUpdateService(stateService, map, db);
    var levels = new LevelService(db);
    var trackService = new TrackService(db, levels);
    var geo = new GeoService(db, levels);
    var tracks = new TracksUpdateService(map, trackService, geo);
    var updates = new MapUpdateService(map, tracks, states);
    var rights = new RightsService(db);
    var spatial = new SpatialDb(db, map, updates, geo, new TestCatalog(), new TestNotifier());

    var events = new EventsService(db, map);
    var grpc = new MapServiceImpl(
      db, map, updates, geo, levels, trackService, states, stateService,
      events, new EventsWriter(db, events), new ValuesService(db), tracks,
      new DiagramService(db), new DiagramTypeService(db), new RightService(db), rights,
      new TestRightsNotifier(),
      new VisualService(db), new FileService(db), spatial, null!,
      new DemoSeeder(
        db, updates, new DiagramService(db), new DiagramTypeService(db), levels, states,
        new RightService(db), geo, new FileService(db), new VisualService(db), spatial, new TestCatalog(),
        TestWiring.DeviceNodes(db, map, updates)),
      TestWiring.Arming(db), TestWiring.Alarms(db, map, states),
      // The attach RPC is not exercised here; the diagram service has its own tests.
      null!);

    return new Services(db, map, geo, spatial, grpc);
  }

  private static async Task<BaseMarkerDTO> Node(
    MapDb map, string name, string? parentId = null, string? ownerId = null)
  {
    var node = new BaseMarkerDTO { name = name, parent_id = parentId, owner_id = ownerId };
    await map.UpdateHierarchyAsync([node]);
    return node;
  }

  private static Space MapSpace(string name) => new()
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

  private static FigureGeoDTO Point(string id, double lat, double lon) => new()
  {
    id = id,
    geometry = new GeometryCircleDTO(new Geo2DCoordDTO { Lat = lat, Lon = lon }),
  };

  private static MapProto.Caller Caller(string? user = null, params string[] roles) => new()
  {
    Username = user ?? string.Empty,
    Roles = { roles },
  };

  private sealed class TestContext : ServerCallContext
  {
    private readonly Metadata _headers = new();
    private readonly Metadata _trailers = new();
    private Status _status;
    private WriteOptions? _writeOptions;

    protected override string MethodCore => string.Empty;
    protected override string HostCore => string.Empty;
    protected override string PeerCore => string.Empty;
    protected override DateTime DeadlineCore => DateTime.MaxValue;
    protected override Metadata RequestHeadersCore => _headers;
    protected override CancellationToken CancellationTokenCore => CancellationToken.None;
    protected override Metadata ResponseTrailersCore => _trailers;
    protected override Status StatusCore { get => _status; set => _status = value; }
    protected override WriteOptions? WriteOptionsCore { get => _writeOptions; set => _writeOptions = value; }
    protected override AuthContext AuthContextCore => new(
      null, new Dictionary<string, List<AuthProperty>>());
    protected override Task WriteResponseHeadersAsyncCore(Metadata responseHeaders) => Task.CompletedTask;
    protected override ContextPropagationToken CreatePropagationTokenCore(ContextPropagationOptions? options) =>
      throw new NotSupportedException();
  }

  [Fact]
  public async Task ACanvasReturnsEveryFigureDrawnOnItAtAnyDepth()
  {
    var s = Create();
    var city = await s.Spatial.UpsertSpaceAsync(MapSpace("City"), CancellationToken.None);
    var otherCity = await s.Spatial.UpsertSpaceAsync(MapSpace("Other city"), CancellationToken.None);

    // A container is an ordinary node: the district holds the building, and neither is a canvas.
    var district = await Node(s.Map, "District", city.Id);
    var building = await Node(s.Map, "Building", district.id);
    var camera = await Node(s.Map, "Camera", building.id);

    // The same numbers on both canvases: only canvas_id can tell them apart.
    var farAway = await Node(s.Map, "Far away", otherCity.Id);

    await s.Geo.CreateGeo([
      Point(district.id!, 0.5, 0.5),
      Point(building.id!, 0.5, 0.5),
      Point(camera.id!, 0.5, 0.5),
      Point(farAway.id!, 0.5, 0.5),
    ]);

    var geo = await s.Geo.GetGeoAsync(new BoxDTO { canvas_id = city.Id });

    Assert.Equal(
      new[] { district.id!, building.id!, camera.id! }.OrderBy(id => id, StringComparer.Ordinal),
      geo.Keys.OrderBy(id => id, StringComparer.Ordinal));
  }

  [Fact]
  public async Task ACanvasReturnsNothingForAnIdThatIsNotACanvas()
  {
    var s = Create();
    var city = await s.Spatial.UpsertSpaceAsync(MapSpace("City"), CancellationToken.None);
    var node = await Node(s.Map, "Node", city.Id);
    await s.Geo.CreateGeo([Point(node.id!, 0.5, 0.5)]);

    var geo = await s.Geo.GetGeoAsync(new BoxDTO { canvas_id = "not-a-canvas" });

    Assert.Empty(geo);
  }

  [Fact]
  public async Task TheCanvasNarrowsTheBoxThatTheIdsAlsoAskFor()
  {
    var s = Create();
    var city = await s.Spatial.UpsertSpaceAsync(MapSpace("City"), CancellationToken.None);
    var otherCity = await s.Spatial.UpsertSpaceAsync(MapSpace("Other city"), CancellationToken.None);
    var here = await Node(s.Map, "Here", city.Id);
    var there = await Node(s.Map, "There", otherCity.Id);

    await s.Geo.CreateGeo([Point(here.id!, 0.5, 0.5), Point(there.id!, 0.5, 0.5)]);

    var geo = await s.Geo.GetGeoAsync(new BoxDTO
    {
      canvas_id = city.Id,
      ids = [here.id!, there.id!],
    });

    Assert.Equal([here.id!], geo.Keys);
  }

  [Fact]
  public async Task GetByBoxOnACanvasKeepsTheRightsFilter()
  {
    var s = Create();
    var city = await s.Spatial.UpsertSpaceAsync(MapSpace("City"), CancellationToken.None);
    var seen = await Node(s.Map, "Seen", city.Id);
    var hidden = await Node(s.Map, "Hidden", city.Id);
    await s.Geo.CreateGeo([Point(seen.id!, 0.5, 0.5), Point(hidden.id!, 0.5, 0.5)]);

    s.Db.Rights.Add(new DBRight
    {
      object_id = Guid.Parse(seen.id!),
      subject = "alice",
      action = RightActions.View,
    });
    await s.Db.SaveChangesAsync();

    var figures = await s.Grpc.GetByBox(new MapProto.GetByBoxRequest
    {
      Box = new MapProto.Box { CanvasId = city.Id },
      Caller = Caller("alice"),
    }, new TestContext());

    Assert.Equal([seen.id!], figures.Figs.Select(figure => figure.Id));
  }
}
