using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using VmsMap.Adapter;
using VmsMap.Clients;
using VmsMap.Db;
using VmsMap.Demo;
using VmsMap.Domain;
using VmsMap.Grpc;
using VmsMap.PubSub;
using MapProto = Vms.Map;

namespace VmsMap.Tests;

// The thin gRPC read layer: the rights filter, the owner rewrite for replicas, the event filter
// and replica creation. The heavy geometry paths need PostgreSQL and live in the spatial tests.
public class MapServiceReadsTests
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
    public int Bumps { get; private set; }

    public Task BumpAsync(CancellationToken ct)
    {
      Bumps++;
      return Task.CompletedTask;
    }
  }

  private sealed record Services(
    VmsMapDbContext Db,
    MapDb Map,
    StatesUpdateService States,
    MapServiceImpl Grpc,
    TestRightsNotifier Notifier);

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
    var notifier = new TestRightsNotifier();
    var spatial = new SpatialDb(db, map, updates, geo, new TestCatalog(), new TestNotifier());

    var events = new EventsService(db, map);
    var grpc = new MapServiceImpl(
      db, map, updates, geo, levels, trackService, states, stateService,
      events, new EventsWriter(db, events), new ValuesService(db), tracks,
      new DiagramService(db), new DiagramTypeService(db), new RightService(db), rights, notifier,
      new VisualService(db), new FileService(db), spatial,
      // The read tests never open a watch stream; the poller needs a scope factory only when it
      // runs, which it does not here.
      null!,
      new DemoSeeder(
        db, updates, new DiagramService(db), new DiagramTypeService(db), levels, states,
        new RightService(db), geo, new FileService(db), new VisualService(db), spatial, new TestCatalog(),
        TestWiring.DeviceNodes(db, map, updates)),
      TestWiring.Arming(db));

    return new Services(db, map, states, grpc, notifier);
  }

  private static async Task<BaseMarkerDTO> Node(
    MapDb map, string name, string? parentId = null, string? ownerId = null)
  {
    var node = new BaseMarkerDTO { name = name, parent_id = parentId, owner_id = ownerId };
    await map.UpdateHierarchyAsync([node]);
    return node;
  }

  private static async Task Grant(
    VmsMapDbContext db, BaseMarkerDTO node, string subject, params string[] actions)
  {
    foreach (var action in actions)
    {
      db.Rights.Add(new DBRight
      {
        object_id = Guid.Parse(node.id!),
        subject = subject,
        action = action,
      });
    }

    await db.SaveChangesAsync();
  }

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
  public async Task UpdateDiagramsKeepsTheGeometryOfADiagramPlacedInARegion()
  {
    var s = Create();
    var rack = await Node(s.Map, "Rack");
    var computer = await Node(s.Map, "Switch", rack.id);

    await s.Grpc.UpdateDiagrams(new MapProto.DiagramList
    {
      Diagrams =
      {
        new MapProto.Diagram
        {
          Id = computer.id!,
          RegionId = "slot-1",
          Geometry = new MapProto.DiagramCoordinate { Width = 320, Height = 240 },
        },
      },
    }, new TestContext());

    // A region gives the diagram its place in the rack; the geometry is still its own canvas.
    var read = await s.Grpc.GetDiagramsByIds(new MapProto.GetByIdsRequest
    {
      Ids = { computer.id! },
      Caller = Caller("admin", "admin"),
    }, new TestContext());

    var stored = Assert.Single(read.Diagrams);
    Assert.Equal("slot-1", stored.RegionId);
    Assert.NotNull(stored.Geometry);
    Assert.Equal(320, stored.Geometry.Width);
    Assert.Equal(240, stored.Geometry.Height);

    // Rewriting the placement keeps the canvas beside the region too.
    await s.Grpc.UpdateDiagrams(new MapProto.DiagramList
    {
      Diagrams =
      {
        new MapProto.Diagram
        {
          Id = computer.id!,
          RegionId = "slot-2",
          Geometry = new MapProto.DiagramCoordinate { Width = 400, Height = 300 },
        },
      },
    }, new TestContext());

    var rewritten = Assert.Single(await s.Db.Diagrams.ToListAsync());
    Assert.Equal("slot-2", rewritten.region_id);
    Assert.Equal(400, rewritten.geometry?.width);
    Assert.Equal(300, rewritten.geometry?.height);
  }

  [Fact]
  public async Task UpdateDiagramTypesNeedsAKeyPerRegionAndNoRepeats()
  {
    var s = Create();

    static MapProto.DiagramTypeRegion Region(string key) => new()
    {
      RegionKey = key,
      Geometry = new MapProto.DiagramCoordinate { Width = 0.5, Height = 0.5 },
    };

    // A child names its place by the key; a blank one names nothing and two alike name the same.
    await Assert.ThrowsAsync<RpcException>(() => s.Grpc.UpdateDiagramTypes(new MapProto.DiagramTypeList
    {
      DiagramTypes = { new MapProto.DiagramType { Name = "Rack", Regions = { Region("  ") } } },
    }, new TestContext()));

    await Assert.ThrowsAsync<RpcException>(() => s.Grpc.UpdateDiagramTypes(new MapProto.DiagramTypeList
    {
      DiagramTypes = { new MapProto.DiagramType { Name = "Rack", Regions = { Region("slot-1"), Region("slot-1") } } },
    }, new TestContext()));

    var written = await s.Grpc.UpdateDiagramTypes(new MapProto.DiagramTypeList
    {
      DiagramTypes = { new MapProto.DiagramType { Name = "Rack", Regions = { Region(" slot-1 ") } } },
    }, new TestContext());

    // The key is stored trimmed: it is what a child's region_id is compared with.
    var type = Assert.Single(written.DiagramTypes);
    Assert.Equal("slot-1", Assert.Single(type.Regions).RegionKey);
  }

  [Fact]
  public async Task GetByParentHidesANodeTheCallerCannotSee()
  {
    var s = Create();
    var building = await Node(s.Map, "Building");
    var camera = await Node(s.Map, "Camera", building.id);
    await Grant(s.Db, building, "alice", RightActions.View);

    var context = new TestContext();
    var seen = await s.Grpc.GetByParent(new MapProto.GetByParentRequest
    {
      ParentId = building.id,
      Caller = Caller("alice"),
    }, context);

    Assert.Single(seen.Children);
    Assert.Equal(camera.id, seen.Children[0].Id);

    var hidden = await s.Grpc.GetByParent(new MapProto.GetByParentRequest
    {
      ParentId = building.id,
      Caller = Caller("bob"),
    }, context);

    Assert.Empty(hidden.Children);
    Assert.Empty(hidden.Parents);
  }

  [Fact]
  public async Task GetObjPropsReportsWhetherTheNodeHasChildren()
  {
    var s = Create();
    var container = await Node(s.Map, "District");
    await Node(s.Map, "Building", container.id);

    var props = await s.Grpc.GetObjProps(new MapProto.GetObjPropsRequest
    {
      Id = container.id,
      Caller = Caller("admin", "admin"),
    }, new TestContext());

    // The panel that opens on this read replaces the node in the client's store, and a container
    // without this flag loses its "enter" action.
    Assert.True(props.Marker.HasChildren);
  }

  [Fact]
  public async Task UpdateFiguresKeepsTheOwnerAndTheLayerItWasGiven()
  {
    var s = Create();
    var camera = await Node(s.Map, "Camera");
    var plan = await Node(s.Map, "Plan");
    var replica = await Node(s.Map, "Camera", plan.id, ownerId: camera.id);

    await s.Grpc.UpdateFigures(new MapProto.Figures
    {
      Figs =
      {
        new MapProto.Figure
        {
          Id = replica.id!,
          ParentId = plan.id!,
          OwnerId = camera.id!,
          Name = "Camera",
          ZoomLevel = "district",
          Geometry = new MapProto.Geometry
          {
            Type = "Point",
            Coord = { new MapProto.Coordinate { Lat = 0.5, Lon = 0.5 } },
          },
        },
      },
    }, new TestContext());

    // A figure write also writes the tree row: the owner has to travel back or a picture loses it.
    var stored = await s.Db.Markers.SingleAsync(m => m.id == Guid.Parse(replica.id!));
    Assert.Equal(Guid.Parse(camera.id!), stored.owner_id);

    var figure = await s.Db.GeoObjects.SingleAsync(g => g.id == Guid.Parse(replica.id!));
    Assert.Equal("district", figure.zoom_level);
  }

  [Fact]
  public async Task GetAlarmedStatesWithoutIdsReturnsEveryAlarmTheCallerSees()
  {
    var s = Create();
    var camera = await Node(s.Map, "Camera");
    var hidden = await Node(s.Map, "Hidden");
    await Grant(s.Db, camera, "alice", RightActions.View);
    s.Db.AlarmStates.Add(new DBAlarmState
    {
      id = Guid.Parse(camera.id!),
      alarm = true,
      children_alarms = 0,
    });
    s.Db.AlarmStates.Add(new DBAlarmState
    {
      id = Guid.Parse(hidden.id!),
      alarm = true,
      children_alarms = 0,
    });
    await s.Db.SaveChangesAsync();

    var seen = await s.Grpc.GetAlarmedStates(new MapProto.GetStatesRequest
    {
      Caller = Caller("alice"),
    }, new TestContext());

    Assert.Contains(seen.States, state => state.Id == camera.id && state.Alarm);
    Assert.DoesNotContain(seen.States, state => state.Id == hidden.id);
  }

  [Fact]
  public async Task GetVisualStatesRewritesTheOwnerStateForAReplica()
  {
    var s = Create();
    var camera = await Node(s.Map, "Camera");
    var plan = await Node(s.Map, "Plan");
    var replica = await Node(s.Map, "Camera", plan.id, ownerId: camera.id);
    await Grant(s.Db, camera, "alice", RightActions.View);
    await s.States.UpdateStates([
      new ObjectStateDTO { id = camera.id, states = [ "ALARM" ] }
    ]);

    var states = await s.Grpc.GetVisualStates(new MapProto.GetStatesRequest
    {
      Ids = { camera.id!, replica.id! },
      Caller = Caller("alice"),
    }, new TestContext());

    Assert.Equal(2, states.States.Count);
    Assert.All(states.States, state => Assert.Contains("ALARM", state.States));
    Assert.Contains(states.States, state => state.Id == replica.id);
  }

  [Fact]
  public async Task GetEventsByFilterHidesEventsOnAnInvisibleNode()
  {
    var s = Create();
    var camera = await Node(s.Map, "Camera");
    await Grant(s.Db, camera, "alice", RightActions.View);
    await new EventsService(s.Db, s.Map).InsertManyAsync([
      new EventDTO
      {
        id = Guid.NewGuid().ToString(),
        object_id = camera.id,
        event_name = "alarm",
        timestamp = DateTime.UtcNow,
        param0 = "main",
        param1 = "motion",
      }
    ]);

    var alice = await s.Grpc.GetEventsByFilter(new MapProto.GetEventsByFilterRequest
    {
      Caller = Caller("alice"),
    }, new TestContext());
    var bob = await s.Grpc.GetEventsByFilter(new MapProto.GetEventsByFilterRequest
    {
      Caller = Caller("bob"),
    }, new TestContext());

    Assert.Single(alice.Events);
    Assert.Empty(bob.Events);
  }

  [Fact]
  public async Task CreateReplicaCopiesTheOwnerPropertiesAndNeedsEditOnTheParent()
  {
    var s = Create();
    var building = await Node(s.Map, "Building");
    var camera = await Node(s.Map, "Camera", building.id);
    await Grant(s.Db, building, "alice", RightActions.View, RightActions.Edit);
    await ((IMapService)s.Map).UpdatePropAsync([
      new ObjPropsDTO
      {
        id = camera.id,
        extra_props = [new ObjExtraPropertyDTO { prop_name = "model", str_val = "X" }],
      }
    ]);

    var replica = await s.Grpc.CreateReplica(new MapProto.CreateReplicaRequest
    {
      OwnerId = camera.id,
      ParentId = building.id,
      Caller = Caller("alice"),
    }, new TestContext());

    Assert.NotEqual(camera.id, replica.Id);
    Assert.Equal(camera.id, replica.OwnerId);
    var props = await s.Map.GetPropAsync(replica.Id);
    Assert.Contains(props.extra_props!, p => p.prop_name == "model" && p.str_val == "X");

    await Assert.ThrowsAsync<RpcException>(() =>
      s.Grpc.CreateReplica(new MapProto.CreateReplicaRequest
      {
        OwnerId = camera.id,
        ParentId = building.id,
        Caller = Caller("bob"),
      }, new TestContext()));
  }

  [Fact]
  public async Task GetByParentNamesTheKindOfEveryChild()
  {
    var s = Create();
    var root = await Node(s.Map, "Root");
    var space = await Node(s.Map, "Floor", root.id);
    var diagram = await Node(s.Map, "Rack", root.id);
    var landmark = await Node(s.Map, "Door", root.id);
    var camera = await Node(s.Map, "Camera", root.id);
    var figure = await Node(s.Map, "Box", root.id);
    var plain = await Node(s.Map, "Note", root.id);
    var replica = await Node(s.Map, "Camera picture", root.id, ownerId: camera.id);

    s.Db.Spaces.Add(new DBSpace
    {
      object_id = Guid.Parse(space.id!),
      kind = SpaceKinds.Plan,
    });
    s.Db.Diagrams.Add(new DBDiagram
    {
      id = Guid.Parse(diagram.id!),
      dgr_type = Guid.NewGuid(),
      geometry = new DBDiagramCoord { width = 100, height = 100 },
    });
    s.Db.Landmarks.Add(new DBLandmark { object_id = Guid.Parse(landmark.id!), height_m = 1 });
    s.Db.DeviceNodes.Add(TestCameras.DeviceNode(TestCameras.One, Guid.Parse(camera.id!)));
    s.Db.GeoObjects.Add(new DBGeoObject
    {
      id = Guid.Parse(figure.id!),
      figure = new NetTopologySuite.Geometries.Point(0, 0),
    });
    await s.Db.SaveChangesAsync();

    var response = await s.Grpc.GetByParent(new MapProto.GetByParentRequest
    {
      ParentId = root.id,
      Caller = Caller("alice", "admin"),
    }, new TestContext());

    string KindOf(BaseMarkerDTO node) =>
      response.Children.Single(c => c.Id == node.id).NodeKind;

    Assert.Equal("space", KindOf(space));
    Assert.Equal("diagram", KindOf(diagram));
    Assert.Equal("landmark", KindOf(landmark));
    Assert.Equal("camera", KindOf(camera));
    Assert.Equal("figure", KindOf(figure));
    Assert.Equal("object", KindOf(plain));
    // A replica answers with its owner's kind: the picture of a camera is still that camera.
    Assert.Equal("camera", KindOf(replica));
  }

  [Fact]
  public async Task GetByIdsReturnsTheVisualAndKindOfAFigure()
  {
    var s = Create();
    var root = await Node(s.Map, "Root");
    var figure = await Node(s.Map, "Box", root.id);
    var guid = Guid.Parse(figure.id!);

    s.Db.GeoObjects.Add(new DBGeoObject
    {
      id = guid,
      figure = new NetTopologySuite.Geometries.Point(0, 0),
      radius = 5,
      zoom_level = "city",
    });
    s.Db.Visuals.Add(new DBVisual
    {
      object_id = guid,
      color = "#123456",
      text = "Door",
      rotation_deg = 45,
    });
    await s.Db.SaveChangesAsync();

    var figures = await s.Grpc.GetByIds(new MapProto.GetByIdsRequest
    {
      Ids = { figure.id! },
      Caller = Caller("alice", "admin"),
    }, new TestContext());

    var fig = Assert.Single(figures.Figs);
    Assert.Equal("figure", fig.NodeKind);
    Assert.Equal("#123456", fig.Visual.Color);
    Assert.Equal("Door", fig.Visual.Text);
    Assert.True(fig.Visual.HasRotationDeg);
    Assert.Equal(45, fig.Visual.RotationDeg);
  }
}
