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
    var updates = new MapUpdateService(db, map, tracks, states);
    var rights = new RightsService(db);
    var spatial = new SpatialDb(db, map, updates, geo, new TestCatalog(), new TestNotifier(), TestWiring.CameraArming(db, map));

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
        TestWiring.DeviceNodes(db, map, updates), TestWiring.ObjectSettings(db),
        TestWiring.DeviceDiagrams(db, map, updates, geo)),
      TestWiring.Arming(db), TestWiring.NodeArming(db, map),
      null!, // the settings card has its own tests
      TestWiring.ControlObjects(db, map, rights),
      TestWiring.Alarms(db, map, states),
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

  // The fixtures these tests share. Space and node names are labels no test asserts; the users and
  // the role are the subject the rights cases agree on.
  private const string CityName = "city";
  private const string OtherCityName = "other city";
  private const string NodeLabel = "node";
  private const string NodeNamePrefix = "node ";
  private const string AliceName = "alice";
  private const string AdminName = "root";
  private const string AdminRole = "admin";

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
    var city = await s.Spatial.UpsertSpaceAsync(MapSpace(CityName), CancellationToken.None);
    var otherCity = await s.Spatial.UpsertSpaceAsync(MapSpace(OtherCityName), CancellationToken.None);

    // A container is an ordinary node: the district holds the building, and neither is a canvas.
    var district = await Node(s.Map, "district", city.Id);
    var building = await Node(s.Map, "building", district.id);
    var camera = await Node(s.Map, "camera", building.id);

    // The same numbers on both canvases: only canvas_id can tell them apart.
    var farAway = await Node(s.Map, "far away", otherCity.Id);

    await s.Geo.CreateGeo([
      Point(district.id!, 0.5, 0.5),
      Point(building.id!, 0.5, 0.5),
      Point(camera.id!, 0.5, 0.5),
      Point(farAway.id!, 0.5, 0.5),
    ]);

    var geo = (await s.Geo.GetGeoAsync(new BoxDTO { canvas_id = city.Id })).Objects;

    Assert.Equal(
      new[] { district.id!, building.id!, camera.id! }.OrderBy(id => id, StringComparer.Ordinal),
      geo.Keys.OrderBy(id => id, StringComparer.Ordinal));
  }

  [Fact]
  public async Task ACanvasReturnsNothingForAnIdThatIsNotACanvas()
  {
    var s = Create();
    var city = await s.Spatial.UpsertSpaceAsync(MapSpace(CityName), CancellationToken.None);
    var node = await Node(s.Map, NodeLabel, city.Id);
    await s.Geo.CreateGeo([Point(node.id!, 0.5, 0.5)]);

    var geo = (await s.Geo.GetGeoAsync(new BoxDTO { canvas_id = "not-a-canvas" })).Objects;

    Assert.Empty(geo);
  }

  [Fact]
  public async Task TheCanvasNarrowsTheBoxThatTheIdsAlsoAskFor()
  {
    var s = Create();
    var city = await s.Spatial.UpsertSpaceAsync(MapSpace(CityName), CancellationToken.None);
    var otherCity = await s.Spatial.UpsertSpaceAsync(MapSpace(OtherCityName), CancellationToken.None);
    var here = await Node(s.Map, "here", city.Id);
    var there = await Node(s.Map, "there", otherCity.Id);

    await s.Geo.CreateGeo([Point(here.id!, 0.5, 0.5), Point(there.id!, 0.5, 0.5)]);

    var geo = (await s.Geo.GetGeoAsync(new BoxDTO
    {
      canvas_id = city.Id,
      ids = [here.id!, there.id!],
    })).Objects;

    Assert.Equal([here.id!], geo.Keys);
  }

  [Fact]
  public async Task GetByBoxOnACanvasKeepsTheRightsFilter()
  {
    var s = Create();
    var city = await s.Spatial.UpsertSpaceAsync(MapSpace(CityName), CancellationToken.None);
    var seen = await Node(s.Map, "seen", city.Id);
    var hidden = await Node(s.Map, "hidden", city.Id);
    await s.Geo.CreateGeo([Point(seen.id!, 0.5, 0.5), Point(hidden.id!, 0.5, 0.5)]);

    s.Db.Rights.Add(new DBRight
    {
      object_id = Guid.Parse(seen.id!),
      subject = AliceName,
      action = RightActions.View,
    });
    await s.Db.SaveChangesAsync();

    var figures = await s.Grpc.GetByBox(new MapProto.GetByBoxRequest
    {
      Box = new MapProto.Box { CanvasId = city.Id },
      Caller = Caller(AliceName),
    }, new TestContext());

    Assert.Equal([seen.id!], figures.Figs.Select(figure => figure.Id));
  }

  [Fact]
  public async Task ACutCanvasSaysSoAndKeepsTheSamePrefixEveryTime()
  {
    const int pageSize = 2;
    const int figureCount = pageSize + 1; // one figure beyond the cut

    var s = Create();
    var city = await s.Spatial.UpsertSpaceAsync(MapSpace(CityName), CancellationToken.None);

    var nodes = new List<BaseMarkerDTO>();
    for (var i = 0; i < figureCount; i++)
      nodes.Add(await Node(s.Map, NodeNamePrefix + i, city.Id));
    await s.Geo.CreateGeo(nodes.Select(n => Point(n.id!, 0.5, 0.5)).ToList());

    var first = await s.Geo.GetGeoAsync(new BoxDTO { canvas_id = city.Id, count = pageSize });
    var second = await s.Geo.GetGeoAsync(new BoxDTO { canvas_id = city.Id, count = pageSize });

    Assert.True(first.Truncated);
    Assert.Equal(pageSize, first.Objects.Count);
    Assert.Equal(first.Objects.Keys, second.Objects.Keys);
  }

  [Fact]
  public async Task ACanvasThatFitsIsNotTruncated()
  {
    var s = Create();
    var city = await s.Spatial.UpsertSpaceAsync(MapSpace(CityName), CancellationToken.None);
    var node = await Node(s.Map, NodeLabel, city.Id);
    await s.Geo.CreateGeo([Point(node.id!, 0.5, 0.5)]);

    var read = await s.Geo.GetGeoAsync(new BoxDTO { canvas_id = city.Id });

    Assert.False(read.Truncated);
    Assert.Equal([node.id!], read.Objects.Keys);
  }

  [Fact]
  public async Task GetByBoxCarriesTheCutToTheClient()
  {
    const int pageSize = 2;
    const int figureCount = pageSize + 1; // one figure beyond the cut

    var s = Create();
    var city = await s.Spatial.UpsertSpaceAsync(MapSpace(CityName), CancellationToken.None);
    var nodes = new List<BaseMarkerDTO>();
    for (var i = 0; i < figureCount; i++)
      nodes.Add(await Node(s.Map, NodeNamePrefix + i, city.Id));
    await s.Geo.CreateGeo(nodes.Select(n => Point(n.id!, 0.5, 0.5)).ToList());

    var figures = await s.Grpc.GetByBox(new MapProto.GetByBoxRequest
    {
      Box = new MapProto.Box { CanvasId = city.Id, Count = pageSize },
      Caller = Caller(AdminName, AdminRole),
    }, new TestContext());

    Assert.True(figures.Truncated);
    Assert.Equal(pageSize, figures.Figs.Count);
  }

  // The order the pages are read in is the Guid order, not the text of its id.
  private static List<string> OrderedIds(IEnumerable<BaseMarkerDTO> nodes) => nodes
    .Select(n => Guid.Parse(n.id!))
    .OrderBy(id => id)
    .Select(id => Utils.ConvertGuidToObjectId(id))
    .ToList();

  // A cut canvas is whole when its pages are read in turn: every answer names where it ended, and
  // the next starts strictly after it in the same order, so nothing is repeated or skipped.
  [Fact]
  public async Task ACutCanvasIsReadWholeAPageAtATime()
  {
    const int pageSize = 2;
    const int figureCount = pageSize * 2 + 1; // two full pages and a tail

    var s = Create();
    var city = await s.Spatial.UpsertSpaceAsync(MapSpace(CityName), CancellationToken.None);

    var nodes = new List<BaseMarkerDTO>();
    for (var i = 0; i < figureCount; i++)
      nodes.Add(await Node(s.Map, NodeNamePrefix + i, city.Id));
    await s.Geo.CreateGeo(nodes.Select(n => Point(n.id!, 0.5, 0.5)).ToList());

    var ordered = OrderedIds(nodes);

    var page1 = await s.Geo.GetGeoAsync(new BoxDTO { canvas_id = city.Id, count = pageSize });
    var page2 = await s.Geo.GetGeoAsync(new BoxDTO
    {
      canvas_id = city.Id,
      count = pageSize,
      after_id = Guid.Parse(page1.NextCursor!),
    });
    var page3 = await s.Geo.GetGeoAsync(new BoxDTO
    {
      canvas_id = city.Id,
      count = pageSize,
      after_id = Guid.Parse(page2.NextCursor!),
    });

    Assert.True(page1.Truncated);
    Assert.Equal(ordered.Take(pageSize), page1.Objects.Keys);
    Assert.Equal(page1.Objects.Keys.Last(), page1.NextCursor);

    Assert.True(page2.Truncated);
    Assert.Equal(ordered.Skip(pageSize).Take(pageSize), page2.Objects.Keys);
    Assert.Equal(page2.Objects.Keys.Last(), page2.NextCursor);

    Assert.False(page3.Truncated);
    Assert.Equal(ordered.Skip(pageSize * 2), page3.Objects.Keys);
    Assert.Null(page3.NextCursor);

    Assert.Equal(
      ordered,
      page1.Objects.Keys.Concat(page2.Objects.Keys).Concat(page3.Objects.Keys));
  }

  [Fact]
  public async Task ACursorAtTheLastFigureIsAnEmptyTail()
  {
    var s = Create();
    var city = await s.Spatial.UpsertSpaceAsync(MapSpace(CityName), CancellationToken.None);
    var nodes = new List<BaseMarkerDTO>();
    for (var i = 0; i < 2; i++)
      nodes.Add(await Node(s.Map, NodeNamePrefix + i, city.Id));
    await s.Geo.CreateGeo(nodes.Select(n => Point(n.id!, 0.5, 0.5)).ToList());

    var whole = await s.Geo.GetGeoAsync(new BoxDTO { canvas_id = city.Id });
    var last = OrderedIds(nodes).Last();

    var tail = await s.Geo.GetGeoAsync(new BoxDTO
    {
      canvas_id = city.Id,
      after_id = Guid.Parse(last),
    });

    Assert.False(whole.Truncated);
    Assert.Null(whole.NextCursor);
    Assert.Empty(tail.Objects);
    Assert.False(tail.Truncated);
    Assert.Null(tail.NextCursor);
  }

  // The cursor is the read's cut, not the last figure of the answer: a page can end on a figure
  // the caller may not see, and the next page still has to start after it, or the walk would
  // stall on a page that is empty by rights alone.
  [Fact]
  public async Task ACursorNamesTheReadsCutEvenWhenRightsHideIt()
  {
    const int pageSize = 2;
    const int figureCount = pageSize * 2; // a cut page, then a page with nothing left over

    var s = Create();
    var city = await s.Spatial.UpsertSpaceAsync(MapSpace(CityName), CancellationToken.None);

    var nodes = new List<BaseMarkerDTO>();
    for (var i = 0; i < figureCount; i++)
      nodes.Add(await Node(s.Map, NodeNamePrefix + i, city.Id));
    await s.Geo.CreateGeo(nodes.Select(n => Point(n.id!, 0.5, 0.5)).ToList());

    var ordered = OrderedIds(nodes);
    var visibleFirst = ordered.First();
    var visibleLast = ordered.Last();
    foreach (var id in new[] { visibleFirst, visibleLast })
    {
      s.Db.Rights.Add(new DBRight
      {
        object_id = Guid.Parse(id),
        subject = AliceName,
        action = RightActions.View,
      });
    }
    await s.Db.SaveChangesAsync();

    var first = await s.Grpc.GetByBox(new MapProto.GetByBoxRequest
    {
      Box = new MapProto.Box { CanvasId = city.Id, Count = pageSize },
      Caller = Caller(AliceName),
    }, new TestContext());

    Assert.Equal([visibleFirst], first.Figs.Select(figure => figure.Id));
    Assert.True(first.Truncated);
    // The cut is the last row the read took, hidden though it is.
    Assert.Equal(ordered.Take(pageSize).Last(), first.NextCursor);

    var second = await s.Grpc.GetByBox(new MapProto.GetByBoxRequest
    {
      Box = new MapProto.Box { CanvasId = city.Id, Count = pageSize },
      AfterId = first.NextCursor,
      Caller = Caller(AliceName),
    }, new TestContext());

    Assert.Equal([visibleLast], second.Figs.Select(figure => figure.Id));
    Assert.False(second.Truncated);
    Assert.Empty(second.NextCursor);
  }

  [Fact]
  public async Task GetByBoxCarriesTheCursorToTheClient()
  {
    const int pageSize = 1;

    var s = Create();
    var city = await s.Spatial.UpsertSpaceAsync(MapSpace(CityName), CancellationToken.None);
    var nodes = new List<BaseMarkerDTO>();
    for (var i = 0; i < 3; i++)
      nodes.Add(await Node(s.Map, NodeNamePrefix + i, city.Id));
    await s.Geo.CreateGeo(nodes.Select(n => Point(n.id!, 0.5, 0.5)).ToList());

    var ordered = OrderedIds(nodes);

    var first = await s.Grpc.GetByBox(new MapProto.GetByBoxRequest
    {
      Box = new MapProto.Box { CanvasId = city.Id, Count = pageSize },
      Caller = Caller(AdminName, AdminRole),
    }, new TestContext());

    Assert.Equal(ordered.Take(pageSize), first.Figs.Select(figure => figure.Id));
    Assert.True(first.Truncated);
    Assert.Equal(first.Figs.Single().Id, first.NextCursor);

    var second = await s.Grpc.GetByBox(new MapProto.GetByBoxRequest
    {
      Box = new MapProto.Box { CanvasId = city.Id, Count = pageSize },
      AfterId = first.NextCursor,
      Caller = Caller(AdminName, AdminRole),
    }, new TestContext());

    Assert.Equal(ordered.Skip(pageSize).Take(pageSize), second.Figs.Select(figure => figure.Id));
    Assert.True(second.Truncated);
    Assert.Equal(second.Figs.Single().Id, second.NextCursor);
  }

  [Fact]
  public async Task GetByBoxRefusesACursorThatIsNotAUuid()
  {
    var s = Create();
    var city = await s.Spatial.UpsertSpaceAsync(MapSpace(CityName), CancellationToken.None);

    var error = await Assert.ThrowsAsync<RpcException>(() => s.Grpc.GetByBox(
      new MapProto.GetByBoxRequest
      {
        Box = new MapProto.Box { CanvasId = city.Id },
        AfterId = "not-a-uuid",
        Caller = Caller(AdminName, AdminRole),
      }, new TestContext()));

    Assert.Equal(StatusCode.InvalidArgument, error.StatusCode);
  }

  // Centroids are an aggregate of the read, not its prefix, so a clustered answer cannot be
  // continued: the flag still says the rectangle is partial, the cursor is left empty.
  [Fact]
  public async Task AClusteredAnswerGivesNoCursor()
  {
    // The page is also the size at which the read is drawn as centroids rather than as figures.
    const int pageSize = 1000;

    var s = Create();
    var city = await s.Spatial.UpsertSpaceAsync(MapSpace(CityName), CancellationToken.None);

    var nodes = Enumerable.Range(0, pageSize + 1)
      .Select(i => new DBMarker
      {
        id = Guid.NewGuid(),
        parent_id = Guid.Parse(city.Id),
        name = NodeNamePrefix + i,
      })
      .ToList();
    s.Db.Markers.AddRange(nodes);
    await s.Db.SaveChangesAsync();
    await s.Geo.CreateGeo(nodes
      .Select(n => Point(Utils.ConvertGuidToObjectId(n.id), 0.5, 0.5))
      .ToList());

    var figures = await s.Grpc.GetByBox(new MapProto.GetByBoxRequest
    {
      Box = new MapProto.Box
      {
        Wn = new MapProto.Coordinate { Lat = 0, Lon = 0 },
        Es = new MapProto.Coordinate { Lat = 1, Lon = 1 },
        Count = pageSize,
      },
      Caller = Caller(AdminName, AdminRole),
    }, new TestContext());

    Assert.True(figures.Truncated);
    Assert.NotEmpty(figures.Figs);
    Assert.Empty(figures.NextCursor);
  }
}
