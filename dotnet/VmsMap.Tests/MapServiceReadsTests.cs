using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using VmsMap.Clients;
using VmsMap.Db;
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

    var grpc = new MapServiceImpl(
      db, map, updates, geo, levels, trackService, states, stateService,
      new EventsService(db, map), new ValuesService(db), tracks,
      new DiagramService(db), new DiagramTypeService(db), new RightService(db), rights, notifier,
      new VisualService(db), new FileService(db),
      new SpatialDb(db, map, updates, geo, new TestCatalog(), new TestNotifier()),
      // The read tests never open a watch stream; the poller needs a scope factory only when it
      // runs, which it does not here.
      null!);

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
}
