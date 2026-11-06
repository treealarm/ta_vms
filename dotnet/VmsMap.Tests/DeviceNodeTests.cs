using System;
using System.Collections.Generic;
using System.Linq;
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

// The registry's second kind of row. A device is one node (`part_kind = 'device'`); its parts are
// nodes of their own under it. Only the device row makes a camera: a part is a different kind, is
// not named by the camera lookup, and carries no camera id in the rights answer -- while its
// states and events are read like any other node's, with no geometry anywhere.
public class DeviceNodeTests
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

  private sealed class TestContext : ServerCallContext
  {
    protected override string MethodCore => "test";
    protected override string HostCore => "localhost";
    protected override string PeerCore => "peer";
    protected override DateTime DeadlineCore => DateTime.MaxValue;
    protected override Metadata RequestHeadersCore => [];
    protected override CancellationToken CancellationTokenCore => CancellationToken.None;
    protected override Metadata ResponseTrailersCore => [];
    protected override Status StatusCore { get; set; }
    protected override WriteOptions? WriteOptionsCore { get; set; }
    protected override AuthContext AuthContextCore => new(null, new Dictionary<string, List<AuthProperty>>());

    protected override ContextPropagationToken CreatePropagationTokenCore(ContextPropagationOptions? options) =>
      throw new NotSupportedException();

    protected override Task WriteResponseHeadersAsyncCore(Metadata responseHeaders) => Task.CompletedTask;
  }

  private sealed record Services(
    VmsMapDbContext Db, MapDb Map, StatesUpdateService States, DeviceNodeService Nodes, MapServiceImpl Grpc);

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
    var nodes = new DeviceNodeService(db, map, updates, NullLogger<DeviceNodeService>.Instance);

    var grpc = new MapServiceImpl(
      db, map, updates, geo, levels, trackService, states, stateService,
      new EventsService(db, map), new ValuesService(db), tracks,
      new DiagramService(db), new DiagramTypeService(db), new RightService(db), rights,
      new TestRightsNotifier(), new VisualService(db), new FileService(db), spatial,
      null!,
      new DemoSeeder(
        db, updates, new DiagramService(db), new DiagramTypeService(db), levels, states,
        new RightService(db), geo, new FileService(db), new VisualService(db), spatial,
        new TestCatalog(), nodes));

    return new Services(db, map, states, nodes, grpc);
  }

  private static MapProto.Caller Admin() => new() { Roles = { "admin" } };

  private sealed record Device(string CameraId, BaseMarkerDTO CameraNode, string DigitalInput, string Relay);

  private static async Task<Device> SeedDeviceAsync(Services s, string cameraId = TestCameras.One)
  {
    var cameraNode = Guid.Parse(CameraObjectId.For(cameraId));
    await s.Map.UpdateHierarchyAsync([new BaseMarkerDTO { id = cameraNode.ToString(), name = "Camera" }]);

    var input = Guid.Parse(DeviceObjectId.For(
      DeviceObjectId.CameraSource, cameraId, DeviceObjectId.DigitalInputPart, "1"));
    var relay = Guid.Parse(DeviceObjectId.For(
      DeviceObjectId.CameraSource, cameraId, DeviceObjectId.RelayPart, "1"));
    await s.Map.UpdateHierarchyAsync([
      new BaseMarkerDTO { id = input.ToString(), parent_id = cameraNode.ToString(), name = "Input 1" },
      new BaseMarkerDTO { id = relay.ToString(), parent_id = cameraNode.ToString(), name = "Relay 1" },
    ]);

    s.Db.DeviceNodes.Add(TestCameras.DeviceNode(cameraId, cameraNode));
    s.Db.DeviceNodes.Add(new DBDeviceNode
    {
      object_id = input,
      source_kind = DeviceObjectId.CameraSource,
      source_id = cameraId,
      part_kind = DeviceObjectId.DigitalInputPart,
      part_token = "1",
    });
    s.Db.DeviceNodes.Add(new DBDeviceNode
    {
      object_id = relay,
      source_kind = DeviceObjectId.CameraSource,
      source_id = cameraId,
      part_kind = DeviceObjectId.RelayPart,
      part_token = "1",
    });
    await s.Db.SaveChangesAsync();

    return new Device(cameraId, new BaseMarkerDTO { id = cameraNode.ToString() }, input.ToString(), relay.ToString());
  }

  [Fact]
  public void ACameraHasOneDerivationWhateverTheSpelling()
  {
    Assert.Equal(
      CameraObjectId.For(TestCameras.One),
      DeviceObjectId.For(
        DeviceObjectId.CameraSource, TestCameras.One, DeviceObjectId.DevicePart, string.Empty));

    Assert.NotEqual(
      CameraObjectId.For(TestCameras.One),
      DeviceObjectId.For(
        DeviceObjectId.CameraSource, TestCameras.One, DeviceObjectId.DigitalInputPart, "1"));
  }

  [Fact]
  public async Task APartIsNotTheCameraAnywhere()
  {
    var s = Create();
    var device = await SeedDeviceAsync(s);

    var ids = new[] { device.CameraNode.id!, device.DigitalInput, device.Relay };

    var cameras = await CameraIdLookup.ResolveAsync(s.Db, ids, CancellationToken.None);
    Assert.Equal(device.CameraId, cameras[device.CameraNode.id!]);
    Assert.False(cameras.ContainsKey(device.DigitalInput));
    Assert.False(cameras.ContainsKey(device.Relay));

    var kinds = await NodeKindLookup.ResolveAsync(s.Db, ids, CancellationToken.None);
    Assert.Equal(NodeKind.Camera, kinds[device.CameraNode.id!]);
    Assert.Equal(NodeKind.Part, kinds[device.DigitalInput]);
    Assert.Equal(NodeKind.Part, kinds[device.Relay]);

    // A right granted on the device flows down to its parts, but only the device row names a
    // camera: the parts are not "the camera" to a reader of the rights answer.
    await s.Db.Rights.AddRangeAsync(
      new DBRight { object_id = Guid.Parse(device.CameraNode.id!), subject = "alice", action = RightActions.View },
      new DBRight { object_id = Guid.Parse(device.DigitalInput), subject = "alice", action = RightActions.View });
    await s.Db.SaveChangesAsync();

    var effective = await new RightsService(s.Db).GetEffectiveAsync(
      new CallerContext("alice", [], false), CancellationToken.None);

    Assert.Equal(
      device.CameraId,
      effective.Single(n => n.object_id == device.CameraNode.id).camera_id);
    Assert.Null(effective.Single(n => n.object_id == device.DigitalInput).camera_id);
    Assert.Null(effective.Single(n => n.object_id == device.Relay).camera_id);
  }

  [Fact]
  public async Task APartWithoutGeometryIsReadLikeAnyNode()
  {
    var s = Create();
    var device = await SeedDeviceAsync(s);

    await s.States.UpdateStates([
      new ObjectStateDTO { id = device.DigitalInput, states = ["SENSOR_OPEN"] }
    ]);
    await new EventsService(s.Db, s.Map).InsertManyAsync([
      new EventDTO
      {
        object_id = device.DigitalInput,
        event_name = "sensor",
        timestamp = DateTime.UtcNow,
        param0 = "open",
      },
    ]);

    var states = await s.Grpc.GetVisualStates(new MapProto.GetStatesRequest
    {
      Ids = { device.DigitalInput },
      Caller = Admin(),
    }, new TestContext());
    Assert.Contains(states.States, state => state.Id == device.DigitalInput && state.States.Contains("SENSOR_OPEN"));

    var events = await s.Grpc.GetEventsByFilter(new MapProto.GetEventsByFilterRequest
    {
      Objects = { device.DigitalInput },
      Caller = Admin(),
    }, new TestContext());
    Assert.Single(events.Events);
    Assert.Equal(device.DigitalInput, events.Events[0].ObjectId);

    // No pictures, no geometry: the node is readable without either.
    Assert.Empty(await s.Db.GeoObjects.Where(g => g.id == Guid.Parse(device.DigitalInput)).ToListAsync());
    Assert.Empty(await s.Db.CameraPlacements.Where(p => p.object_id == Guid.Parse(device.DigitalInput)).ToListAsync());
  }

  [Fact]
  public async Task DeletingACameraRemovesItsParts()
  {
    var s = Create();
    var device = await SeedDeviceAsync(s);

    await s.Nodes.RemoveCameraAsync(device.CameraId, CancellationToken.None);

    var ids = new[]
    {
      Guid.Parse(device.CameraNode.id!), Guid.Parse(device.DigitalInput), Guid.Parse(device.Relay),
    };
    Assert.Empty(await s.Db.Markers.Where(m => ids.Contains(m.id)).ToListAsync());
    Assert.Empty(await s.Db.DeviceNodes.Where(n => n.source_id == device.CameraId).ToListAsync());
  }
}
