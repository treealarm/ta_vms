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
using VmsMap.Domain.Arming;
using VmsMap.Grpc;
using VmsMap.PubSub;
using MapProto = Vms.Map;

namespace VmsMap.Tests;

// The thin gRPC read layer: the rights filter, the owner rewrite for replicas, the event filter
// and replica creation. The heavy geometry paths need PostgreSQL and live in the spatial tests.
public class MapServiceReadsTests
{
  // The fixtures these tests share.
  private const string AliceName = "alice";
  private const string BobName = "bob";
  private const string AdminRole = "admin";
  private const string CameraNamePrefix = "Camera ";
  private const string CameraLabel = "camera";
  private const string CameraKind = "camera";
  private const string BuildingLabel = "building";
  private const string FloorLabel = "floor";
  private const string PlanLabel = "plan";
  private const string RootLabel = "root";
  private const string BoxLabel = "box";
  private const string DoorLabel = "door";
  private const string RackLabel = "rack";
  private const string NoteLabel = "note";
  private const string DistrictLabel = "district";
  private const string DistrictZoomLevel = "district";
  private const string RearPanelLabel = "camera / rear panel";
  private const string FirstRegionKey = "slot-1";
  private const string SecondRegionKey = "slot-2";
  private const string SensorId = "sensor:1";
  private const string AckComment = "seen";
  private const string AlarmState = "ALARM";
  private const string PanelLabel = "panel";
  private const string ShownPanelLabel = "shown panel";
  private const string CanvasLabel = "canvas";
  private const string PartOwnerLabel = "input owner";
  private const string PartLabel = "input";
  private const double PaperWidth = 640;
  private const double PaperHeight = 480;
  private const string FigureColor = "#123456";
  private const string FigureKind = "figure";
  private const string VisualText = "Door";
  private const string ModelPropName = "model";
  private const string ModelPropValue = "X";
  private const string NotePropName = "note";
  private const string PictureName = "my picture";
  private const string CompactGuidFormat = "N";
  // `device_diagram.source_id` is stored as a UUID, so a camera id here is one too.
  private const string DeviceCameraId = "3f2504e0-4f89-11d3-9a0c-0305e82c3309";

  private sealed class TestCatalog : ICameraCatalog
  {
    public Task<CameraInfo?> GetAsync(string cameraId, CancellationToken ct) =>
      Task.FromResult<CameraInfo?>(new CameraInfo(cameraId, CameraNamePrefix + cameraId, "srv"));

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
    MapUpdateService Updates,
    MapServiceImpl Grpc,
    TestRightsNotifier Notifier,
    AlarmStore Alarms,
    TestCameraState Arming,
    IDeviceDiagramService DeviceDiagrams);

  private static Services Create()
  {
    var options = new DbContextOptionsBuilder<VmsMapDbContext>()
      .UseInMemoryDatabase(Guid.NewGuid().ToString(CompactGuidFormat))
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
    var notifier = new TestRightsNotifier();
    var spatial = new SpatialDb(db, map, updates, geo, new TestCatalog(), new TestNotifier(), TestWiring.CameraArming(db, map));
    var arming = new TestCameraState();
    var alarms = TestWiring.Alarms(db, map, states, arming);

    var events = new EventsService(db, map);
    // One instance for both readers: the device diagram service is what the attach RPC places with,
    // and the demo seeder installs the demo's own diagrams through it.
    var deviceDiagrams = TestWiring.DeviceDiagrams(db, map, updates, geo);
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
        TestWiring.DeviceNodes(db, map, updates), TestWiring.ObjectSettings(db),
        deviceDiagrams),
      TestWiring.Arming(db, arming), TestWiring.NodeArming(db, map, cameraState: arming),
      null!, // the settings RPCs have their own tests
      TestWiring.ControlObjects(db, map, rights), alarms,
      deviceDiagrams);

    return new Services(db, map, states, updates, grpc, notifier, alarms, arming, deviceDiagrams);
  }

  private static async Task<BaseMarkerDTO> Node(
    MapDb map, string name, string? parentId = null, string? ownerId = null)
  {
    var node = new BaseMarkerDTO { name = name, parent_id = parentId, owner_id = ownerId };
    await map.UpdateHierarchyAsync([node]);
    return node;
  }

  /// <summary>A stored diagram row: the type it is drawn from, its own paper, its place.</summary>
  private static Task SeedDiagramAsync(
    VmsMapDbContext db, string objectId, string? typeId = null,
    DiagramCoordDTO? geometry = null, string? regionId = null) =>
    new DiagramService(db).UpdateListAsync([
      new DiagramDTO
      {
        id = objectId,
        dgr_type = typeId,
        geometry = geometry,
        region_id = regionId,
      }
    ]);

  /// <summary>A region of a template: the key a child names, and the share of the paper it takes.</summary>
  private static DBDiagramTypeRegion RegionRow(Guid typeId, string key) => new()
  {
    diagram_type_id = typeId,
    region_key = key,
    geometry = new DBDiagramCoord { left = 0, top = 0, width = 0.5, height = 0.5 },
  };

  /// <summary>
  /// The diagram itself — its paper and one part drawn in a region of it — and an empty canvas
  /// the diagram can be shown on. Returns the ids the tests name.
  /// </summary>
  private static async Task<(BaseMarkerDTO Panel, BaseMarkerDTO Part, BaseMarkerDTO Canvas, string TypeId)>
    SeedPanelAsync(Services s)
  {
    // The template's two regions are where its children stand: a row naming a region the type does
    // not have is refused, so the seed has to offer the keys the tests use.
    var type = new DBDiagramType { id = Guid.NewGuid(), name = PanelLabel };
    type.regions.Add(RegionRow(type.id, FirstRegionKey));
    type.regions.Add(RegionRow(type.id, SecondRegionKey));
    s.Db.DiagramTypes.Add(type);
    await s.Db.SaveChangesAsync();
    var typeId = type.id.ToString();

    var panel = await Node(s.Map, PanelLabel);
    await SeedDiagramAsync(
      s.Db, panel.id!, typeId, new DiagramCoordDTO { width = PaperWidth, height = PaperHeight });

    var partOwner = await Node(s.Map, PartOwnerLabel);
    var part = await Node(s.Map, PartLabel, panel.id, partOwner.id);
    await SeedDiagramAsync(s.Db, part.id!, regionId: FirstRegionKey);

    var canvas = await Node(s.Map, CanvasLabel);
    // The canvas is drawn from the same template: its regions are the keys its children name.
    await SeedDiagramAsync(s.Db, canvas.id!, typeId);

    return (panel, part, canvas, typeId);
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
    // A rack's own template: the regions a switch is placed in are its keys.
    var rackType = new DBDiagramType { id = Guid.NewGuid(), name = RackLabel };
    rackType.regions.Add(RegionRow(rackType.id, FirstRegionKey));
    rackType.regions.Add(RegionRow(rackType.id, SecondRegionKey));
    s.Db.DiagramTypes.Add(rackType);
    await s.Db.SaveChangesAsync();

    var rack = await Node(s.Map, RackLabel);
    await SeedDiagramAsync(s.Db, rack.id!, rackType.id.ToString());
    var computer = await Node(s.Map, "switch", rack.id);

    await s.Grpc.UpdateDiagrams(new MapProto.DiagramList
    {
      Diagrams =
      {
        new MapProto.Diagram
        {
          Id = computer.id!,
          RegionId = FirstRegionKey,
          Geometry = new MapProto.DiagramCoordinate { Width = 320, Height = 240 },
        },
      },
    }, new TestContext());

    // A region gives the diagram its place in the rack; the geometry is still its own canvas.
    var read = await s.Grpc.GetDiagramsByIds(new MapProto.GetByIdsRequest
    {
      Ids = { computer.id! },
      Caller = Caller(AdminRole, AdminRole),
    }, new TestContext());

    var stored = Assert.Single(read.Diagrams);
    Assert.Equal(FirstRegionKey, stored.RegionId);
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
          RegionId = SecondRegionKey,
          Geometry = new MapProto.DiagramCoordinate { Width = 400, Height = 300 },
        },
      },
    }, new TestContext());

    var rewritten = Assert.Single(
      await s.Db.Diagrams.Where(d => d.id == Guid.Parse(computer.id!)).ToListAsync());
    Assert.Equal(SecondRegionKey, rewritten.region_id);
    Assert.Equal(400, rewritten.geometry?.width);
    Assert.Equal(300, rewritten.geometry?.height);
  }

  [Fact]
  public async Task ARepresentationInARegionOpensOnItsOwnersPaperAndChildren()
  {
    var s = Create();
    var (panel, part, canvas, typeId) = await SeedPanelAsync(s);

    // The panel shown in a region of the canvas: its own row says the place and nothing else.
    var shown = await Node(s.Map, ShownPanelLabel, canvas.id, panel.id);
    await SeedDiagramAsync(s.Db, shown.id!, regionId: FirstRegionKey);

    var content = await s.Grpc.GetDiagramContent(new MapProto.GetDiagramContentRequest
    {
      DiagramId = shown.id!,
      Depth = 1,
      Caller = Caller(AdminRole, AdminRole),
    }, new TestContext());

    var row = Assert.Single(content.Content, candidate => candidate.Id == shown.id);
    Assert.True(row.IsRepresentation);
    // The content is the diagram's: its type and its paper, not the copy's.
    Assert.Equal(typeId, row.DgrType);
    Assert.Equal(PaperWidth, row.Geometry!.Width);
    Assert.Equal(PaperHeight, row.Geometry.Height);
    // The place is the copy's, and a region is a place like any other: no `placement` here.
    Assert.Equal(FirstRegionKey, row.RegionId);
    Assert.Null(row.Placement);

    Assert.Contains(content.Children, child => child.Id == part.id);
    Assert.DoesNotContain(content.Children, child => child.Id == panel.id);
    Assert.Contains(content.DgrTypes, type => type.Id == typeId);
  }

  [Fact]
  public async Task ARepresentationDroppedOnThePaperKeepsItsRectangleAndShowsItsOwner()
  {
    var s = Create();
    var (panel, part, canvas, typeId) = await SeedPanelAsync(s);

    var shown = await Node(s.Map, ShownPanelLabel, canvas.id, panel.id);
    await SeedDiagramAsync(s.Db, shown.id!, geometry: new DiagramCoordDTO
    {
      left = 20,
      top = 10,
      width = 100,
      height = 50,
    });

    var content = await s.Grpc.GetDiagramContent(new MapProto.GetDiagramContentRequest
    {
      DiagramId = shown.id!,
      Depth = 1,
      Caller = Caller(AdminRole, AdminRole),
    }, new TestContext());

    var row = Assert.Single(content.Content, candidate => candidate.Id == shown.id);
    Assert.True(row.IsRepresentation);
    Assert.Equal(typeId, row.DgrType);
    // Where it stands and what it opens on are two different rectangles.
    Assert.Equal(100, row.Placement!.Width);
    Assert.Equal(50, row.Placement.Height);
    Assert.Equal(PaperWidth, row.Geometry!.Width);
    Assert.Empty(row.RegionId);

    Assert.Contains(content.Children, child => child.Id == part.id);
  }

  [Fact]
  public async Task AnOrdinaryDiagramIsNotARepresentation()
  {
    var s = Create();
    var (panel, part, _, _) = await SeedPanelAsync(s);

    var content = await s.Grpc.GetDiagramContent(new MapProto.GetDiagramContentRequest
    {
      DiagramId = panel.id!,
      Depth = 1,
      Caller = Caller(AdminRole, AdminRole),
    }, new TestContext());

    var row = Assert.Single(content.Content, candidate => candidate.Id == panel.id);
    Assert.False(row.IsRepresentation);
    Assert.Null(row.Placement);
    Assert.Contains(content.Children, child => child.Id == part.id);
  }

  [Fact]
  public async Task ABoundDiagramIsAnOrdinaryDiagramAgainAfterUnbinding()
  {
    var s = Create();
    var (panel, part, canvas, typeId) = await SeedPanelAsync(s);

    // The administrator's own diagram, bound to a device and shown on a canvas.
    await s.DeviceDiagrams.BindAsync(
      panel.id!, CameraKind, DeviceCameraId, null, IDeviceDiagramService.DefaultSlot, default);
    var shown = await Node(s.Map, ShownPanelLabel, canvas.id, panel.id);
    await SeedDiagramAsync(s.Db, shown.id!, regionId: FirstRegionKey);

    var bound = await s.Grpc.GetDeviceDiagramBinding(
      new MapProto.GetDeviceDiagramBindingRequest { NodeId = panel.id! }, new TestContext());
    Assert.True(bound.Bound);
    Assert.Equal(DeviceCameraId, bound.SourceId);

    Assert.True(await s.DeviceDiagrams.UnbindAsync(panel.id!, default));

    var unbound = await s.Grpc.GetDeviceDiagramBinding(
      new MapProto.GetDeviceDiagramBindingRequest { NodeId = panel.id! }, new TestContext());
    Assert.False(unbound.Bound);

    // The copy stays, and the diagram opens on it as an ordinary one: the same type and children.
    var content = await s.Grpc.GetDiagramContent(new MapProto.GetDiagramContentRequest
    {
      DiagramId = shown.id!,
      Depth = 1,
      Caller = Caller(AdminRole, AdminRole),
    }, new TestContext());

    var row = Assert.Single(content.Content, candidate => candidate.Id == shown.id);
    Assert.True(row.IsRepresentation);
    Assert.Equal(typeId, row.DgrType);
    Assert.Contains(content.Children, child => child.Id == part.id);
  }

  [Fact]
  public async Task ARepresentationDoesNotLeakTheChildrenTheCallerMayNotSee()
  {
    var s = Create();
    var (panel, part, canvas, _) = await SeedPanelAsync(s);
    var shown = await Node(s.Map, ShownPanelLabel, canvas.id, panel.id);
    await SeedDiagramAsync(s.Db, shown.id!, regionId: FirstRegionKey);
    // Alice may open the copy; the part inside the diagram was granted to nobody.
    await Grant(s.Db, shown, AliceName, RightActions.View);

    var content = await s.Grpc.GetDiagramContent(new MapProto.GetDiagramContentRequest
    {
      DiagramId = shown.id!,
      Depth = 1,
      Caller = Caller(AliceName),
    }, new TestContext());

    // The copy opens on the diagram's paper, and what she may not see is not in it.
    Assert.Contains(content.Content, row => row.Id == shown.id);
    Assert.DoesNotContain(content.Children, child => child.Id == part.id);
  }

  [Fact]
  public async Task BindDeviceDiagramRefusesANodeThatIsNotADiagram()
  {
    var s = Create();
    var node = await Node(s.Map, NoteLabel);

    var error = await Assert.ThrowsAsync<RpcException>(() => s.Grpc.BindDeviceDiagram(
      new MapProto.BindDeviceDiagramRequest { NodeId = node.id!, SourceId = DeviceCameraId },
      new TestContext()));

    Assert.Equal(StatusCode.InvalidArgument, error.StatusCode);
    Assert.Contains("not a diagram", error.Status.Detail);
  }

  [Fact]
  public async Task BindDeviceDiagramRefusesARequestWithoutADevice()
  {
    var s = Create();
    var node = await Node(s.Map, NoteLabel);

    var error = await Assert.ThrowsAsync<RpcException>(() => s.Grpc.BindDeviceDiagram(
      new MapProto.BindDeviceDiagramRequest { NodeId = node.id! }, new TestContext()));

    Assert.Equal(StatusCode.InvalidArgument, error.StatusCode);
  }

  [Fact]
  public async Task BindingASecondDiagramForTheSameDeviceIsAPrecondition()
  {
    var s = Create();
    var first = await Node(s.Map, PanelLabel);
    var second = await Node(s.Map, ShownPanelLabel);
    await SeedDiagramAsync(s.Db, first.id!);
    await SeedDiagramAsync(s.Db, second.id!);
    await s.Grpc.BindDeviceDiagram(new MapProto.BindDeviceDiagramRequest
    {
      NodeId = first.id!,
      SourceId = DeviceCameraId,
    }, new TestContext());

    var error = await Assert.ThrowsAsync<RpcException>(() => s.Grpc.BindDeviceDiagram(
      new MapProto.BindDeviceDiagramRequest
      {
        NodeId = second.id!,
        SourceId = DeviceCameraId,
      }, new TestContext()));

    // Well formed, and the map's state refuses it: the device already shows a diagram for the view.
    Assert.Equal(StatusCode.FailedPrecondition, error.StatusCode);
    Assert.Contains("already has a diagram", error.Status.Detail);
  }

  [Fact]
  public async Task UnbindDeviceDiagramRefusesARequestWithoutADiagram()
  {
    var s = Create();

    var error = await Assert.ThrowsAsync<RpcException>(() => s.Grpc.UnbindDeviceDiagram(
      new MapProto.UnbindDeviceDiagramRequest(), new TestContext()));

    Assert.Equal(StatusCode.InvalidArgument, error.StatusCode);
  }

  [Fact]
  public async Task AReplicaOfSomethingThatIsNotADiagramReportsNoRow()
  {
    var s = Create();
    // A device part and its picture on the canvas: a replica, but not one of a diagram.
    var owner = await Node(s.Map, PartOwnerLabel);
    var canvas = await Node(s.Map, CanvasLabel);
    // Any type at all: the canvas is a diagram because it has a row, and the copy below names no
    // region, so the template does not matter.
    await SeedDiagramAsync(s.Db, canvas.id!, Guid.NewGuid().ToString());
    var copy = await Node(s.Map, PartLabel, canvas.id, owner.id);

    var content = await s.Grpc.GetDiagramContent(new MapProto.GetDiagramContentRequest
    {
      DiagramId = copy.id!,
      Depth = 1,
      Caller = Caller(AdminRole, AdminRole),
    }, new TestContext());

    Assert.DoesNotContain(content.Content, row => row.Id == copy.id);

    var read = await s.Grpc.GetDiagramsByIds(new MapProto.GetByIdsRequest
    {
      Ids = { copy.id! },
      Caller = Caller(AdminRole, AdminRole),
    }, new TestContext());

    Assert.Empty(read.Diagrams);
  }

  [Fact]
  public async Task RowsFetchedByIdResolveARepresentationTheSameWay()
  {
    var s = Create();
    var (panel, _, canvas, typeId) = await SeedPanelAsync(s);

    var shown = await Node(s.Map, ShownPanelLabel, canvas.id, panel.id);
    await SeedDiagramAsync(s.Db, shown.id!, regionId: FirstRegionKey);

    var read = await s.Grpc.GetDiagramsByIds(new MapProto.GetByIdsRequest
    {
      Ids = { shown.id! },
      Caller = Caller(AdminRole, AdminRole),
    }, new TestContext());

    var row = Assert.Single(read.Diagrams);
    Assert.True(row.IsRepresentation);
    Assert.Equal(typeId, row.DgrType);
    Assert.Equal(FirstRegionKey, row.RegionId);
    Assert.Null(row.Placement);
  }

  [Fact]
  public async Task ARepresentationsTypeIsNotWritableThroughItsOwnRow()
  {
    var s = Create();
    var (panel, _, canvas, typeId) = await SeedPanelAsync(s);

    var shown = await Node(s.Map, ShownPanelLabel, canvas.id, panel.id);
    await SeedDiagramAsync(s.Db, shown.id!, regionId: FirstRegionKey);

    var rejected = await Assert.ThrowsAsync<RpcException>(() => s.Grpc.UpdateDiagrams(
      new MapProto.DiagramList
      {
        Diagrams = { new MapProto.Diagram { Id = shown.id!, DgrType = typeId } },
      }, new TestContext()));
    Assert.Equal(StatusCode.InvalidArgument, rejected.StatusCode);

    // Its place is its own, and moving it is what a copy is for.
    await s.Grpc.UpdateDiagrams(new MapProto.DiagramList
    {
      Diagrams = { new MapProto.Diagram { Id = shown.id!, RegionId = SecondRegionKey } },
    }, new TestContext());

    var stored = await s.Db.Diagrams.SingleAsync(d => d.id == Guid.Parse(shown.id!));
    Assert.Null(stored.dgr_type);
    Assert.Equal(SecondRegionKey, stored.region_id);
  }

  [Fact]
  public async Task DeletingADiagramTakesItsRepresentationsFromEveryCanvas()
  {
    var s = Create();
    var (panel, part, canvas, _) = await SeedPanelAsync(s);

    var shown = await Node(s.Map, ShownPanelLabel, canvas.id, panel.id);
    await SeedDiagramAsync(s.Db, shown.id!, regionId: FirstRegionKey);

    await s.Grpc.DeleteObjects(new MapProto.ObjectIds { Ids = { panel.id! } }, new TestContext());

    // `owner_id` has no foreign key: nothing but this takes the copy away.
    Assert.False(await s.Db.Markers.AnyAsync(m => m.id == Guid.Parse(shown.id!)));
    Assert.False(await s.Db.Markers.AnyAsync(m => m.id == Guid.Parse(part.id!)));
    // The canvas it stood on is not the diagram's: a copy is not the place it is shown in.
    Assert.True(await s.Db.Markers.AnyAsync(m => m.id == Guid.Parse(canvas.id!)));
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
      DiagramTypes = { new MapProto.DiagramType { Name = RackLabel, Regions = { Region("  ") } } },
    }, new TestContext()));

    await Assert.ThrowsAsync<RpcException>(() => s.Grpc.UpdateDiagramTypes(new MapProto.DiagramTypeList
    {
      DiagramTypes = { new MapProto.DiagramType { Name = RackLabel, Regions = { Region(FirstRegionKey), Region(FirstRegionKey) } } },
    }, new TestContext()));

    var written = await s.Grpc.UpdateDiagramTypes(new MapProto.DiagramTypeList
    {
      DiagramTypes = { new MapProto.DiagramType { Name = RackLabel, Regions = { Region(" slot-1 ") } } },
    }, new TestContext());

    // The key is stored trimmed: it is what a child's region_id is compared with.
    var type = Assert.Single(written.DiagramTypes);
    Assert.Equal(FirstRegionKey, Assert.Single(type.Regions).RegionKey);
  }

  [Fact]
  public async Task GetByParentHidesANodeTheCallerCannotSee()
  {
    var s = Create();
    var building = await Node(s.Map, BuildingLabel);
    var camera = await Node(s.Map, CameraLabel, building.id);
    await Grant(s.Db, building, AliceName, RightActions.View);

    var context = new TestContext();
    var seen = await s.Grpc.GetByParent(new MapProto.GetByParentRequest
    {
      ParentId = building.id,
      Caller = Caller(AliceName),
    }, context);

    Assert.Single(seen.Children);
    Assert.Equal(camera.id, seen.Children.Single().Id);

    var hidden = await s.Grpc.GetByParent(new MapProto.GetByParentRequest
    {
      ParentId = building.id,
      Caller = Caller(BobName),
    }, context);

    Assert.Empty(hidden.Children);
    Assert.Empty(hidden.Parents);
  }

  [Fact]
  public async Task GetObjPropsReportsWhetherTheNodeHasChildren()
  {
    var s = Create();
    var container = await Node(s.Map, DistrictLabel);
    await Node(s.Map, BuildingLabel, container.id);

    var props = await s.Grpc.GetObjProps(new MapProto.GetObjPropsRequest
    {
      Id = container.id,
      Caller = Caller(AdminRole, AdminRole),
    }, new TestContext());

    // The panel that opens on this read replaces the node in the client's store, and a container
    // without this flag loses its "enter" action.
    Assert.True(props.Marker.HasChildren);
  }

  [Fact]
  public async Task GetObjPropsNamesTheDeviceADiagramDraws()
  {
    var s = Create();
    var deviceId = CameraObjectId.For(TestCameras.One);
    await s.Map.UpdateHierarchyAsync([new BaseMarkerDTO { id = deviceId, name = CameraNamePrefix + TestCameras.One }]);
    var diagram = await Node(s.Map, RearPanelLabel);
    s.Db.DeviceDiagrams.Add(new DBDeviceDiagram
    {
      object_id = Guid.Parse(diagram.id!),
      source_kind = DeviceObjectId.CameraSource,
      source_id = TestCameras.One,
      slot = IDeviceDiagramService.DefaultSlot,
    });
    await s.Db.SaveChangesAsync();

    var props = await s.Grpc.GetObjProps(new MapProto.GetObjPropsRequest
    {
      Id = diagram.id,
      Caller = Caller(AdminRole, AdminRole),
    }, new TestContext());

    // The panel says whose diagram it is, and can send the reader to the device: the registry row
    // is the only place that knows, and the camera's node id is derived from the camera.
    Assert.Equal(DeviceObjectId.CameraSource, props.Device.SourceKind);
    Assert.Equal(TestCameras.One, props.Device.SourceId);
    Assert.Equal(deviceId, props.Device.NodeId);
    Assert.Equal(CameraNamePrefix + TestCameras.One, props.Device.Name);
  }

  [Fact]
  public async Task GetObjPropsDoesNotNameTheDeviceTheCallerMayNotSee()
  {
    var s = Create();
    var deviceId = CameraObjectId.For(TestCameras.One);
    // The device's canonical node hangs under a server whose rights name somebody else; the
    // diagram is allowed on its own. Seeing the diagram is not seeing the device.
    var server = await Node(s.Map, "server");
    await Grant(s.Db, server, BobName, RightActions.View);
    await s.Map.UpdateHierarchyAsync([
      new BaseMarkerDTO { id = deviceId, parent_id = server.id, name = CameraNamePrefix + TestCameras.One }
    ]);

    var diagram = await Node(s.Map, RearPanelLabel);
    await Grant(s.Db, diagram, AliceName, RightActions.View);
    s.Db.DeviceDiagrams.Add(new DBDeviceDiagram
    {
      object_id = Guid.Parse(diagram.id!),
      source_kind = DeviceObjectId.CameraSource,
      source_id = TestCameras.One,
      slot = IDeviceDiagramService.DefaultSlot,
    });
    await s.Db.SaveChangesAsync();

    var props = await s.Grpc.GetObjProps(new MapProto.GetObjPropsRequest
    {
      Id = diagram.id,
      Caller = Caller(AliceName),
    }, new TestContext());

    // A node the caller may not see never leaves the service: no node id, and no name to go with
    // it. The panel draws no link to the device.
    Assert.Equal(DeviceObjectId.CameraSource, props.Device.SourceKind);
    Assert.Empty(props.Device.NodeId);
    Assert.Empty(props.Device.Name);
  }

  [Fact]
  public async Task UpdateFiguresKeepsTheOwnerAndTheLayerItWasGiven()
  {
    var s = Create();
    var camera = await Node(s.Map, CameraLabel);
    var plan = await Node(s.Map, PlanLabel);
    var replica = await Node(s.Map, CameraLabel, plan.id, ownerId: camera.id);

    await s.Grpc.UpdateFigures(new MapProto.Figures
    {
      Figs =
      {
        new MapProto.Figure
        {
          Id = replica.id!,
          ParentId = plan.id!,
          OwnerId = camera.id!,
          Name = CameraLabel,
          ZoomLevel = DistrictZoomLevel,
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
    Assert.Equal(DistrictZoomLevel, figure.zoom_level);
  }

  [Fact]
  public async Task GetAlarmedStatesWithoutIdsReturnsEveryAlarmTheCallerSees()
  {
    var s = Create();
    var camera = await Node(s.Map, CameraLabel);
    var hidden = await Node(s.Map, "hidden");
    await Grant(s.Db, camera, AliceName, RightActions.View);
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
      Caller = Caller(AliceName),
    }, new TestContext());

    Assert.Contains(seen.States, state => state.Id == camera.id && state.Alarm);
    Assert.DoesNotContain(seen.States, state => state.Id == hidden.id);
  }

  [Fact]
  public async Task GetVisualStatesRewritesTheOwnerStateForAReplica()
  {
    var s = Create();
    var camera = await Node(s.Map, CameraLabel);
    var plan = await Node(s.Map, PlanLabel);
    var replica = await Node(s.Map, CameraLabel, plan.id, ownerId: camera.id);
    await Grant(s.Db, camera, AliceName, RightActions.View);
    await s.States.UpdateStates([
      new ObjectStateDTO { id = camera.id, states = [ AlarmState ] }
    ]);

    var states = await s.Grpc.GetVisualStates(new MapProto.GetStatesRequest
    {
      Ids = { camera.id!, replica.id! },
      Caller = Caller(AliceName),
    }, new TestContext());

    Assert.Equal(2, states.States.Count);
    Assert.All(states.States, state => Assert.Contains(AlarmState, state.States));
    Assert.Contains(states.States, state => state.Id == replica.id);
  }

  [Fact]
  public async Task GetEventsByFilterHidesEventsOnAnInvisibleNode()
  {
    var s = Create();
    var camera = await Node(s.Map, CameraLabel);
    await Grant(s.Db, camera, AliceName, RightActions.View);
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
      Caller = Caller(AliceName),
    }, new TestContext());
    var bob = await s.Grpc.GetEventsByFilter(new MapProto.GetEventsByFilterRequest
    {
      Caller = Caller(BobName),
    }, new TestContext());

    Assert.Single(alice.Events);
    Assert.Empty(bob.Events);
  }

  [Fact]
  public async Task CreateReplicaCopiesTheOwnerPropertiesAndNeedsEditOnTheParent()
  {
    var s = Create();
    var building = await Node(s.Map, BuildingLabel);
    var camera = await Node(s.Map, CameraLabel, building.id);
    await Grant(s.Db, building, AliceName, RightActions.View, RightActions.Edit);
    await ((IMapService)s.Map).UpdatePropAsync([
      new ObjPropsDTO
      {
        id = camera.id,
        extra_props = [new ObjExtraPropertyDTO { prop_name = ModelPropName, str_val = ModelPropValue }],
      }
    ]);

    var replica = await s.Grpc.CreateReplica(new MapProto.CreateReplicaRequest
    {
      OwnerId = camera.id,
      ParentId = building.id,
      Caller = Caller(AliceName),
    }, new TestContext());

    Assert.NotEqual(camera.id, replica.Id);
    Assert.Equal(camera.id, replica.OwnerId);
    // Unnamed, the replica is named by its owner and its own number — the number the server picks.
    Assert.Equal(CameraLabel + " rep1", replica.Name);
    var props = await s.Map.GetPropAsync(replica.Id);
    Assert.Contains(props.extra_props!, p => p.prop_name == ModelPropName && p.str_val == ModelPropValue);

    var second = await s.Grpc.CreateReplica(new MapProto.CreateReplicaRequest
    {
      OwnerId = camera.id,
      ParentId = building.id,
      Name = PictureName,
      Caller = Caller(AliceName),
    }, new TestContext());
    Assert.Equal(PictureName, second.Name);

    await Assert.ThrowsAsync<RpcException>(() =>
      s.Grpc.CreateReplica(new MapProto.CreateReplicaRequest
      {
        OwnerId = camera.id,
        ParentId = building.id,
        Caller = Caller(BobName),
      }, new TestContext()));
  }

  [Fact]
  public async Task GetByParentNamesTheKindOfEveryChild()
  {
    var s = Create();
    var root = await Node(s.Map, RootLabel);
    var space = await Node(s.Map, FloorLabel, root.id);
    var diagram = await Node(s.Map, RackLabel, root.id);
    var landmark = await Node(s.Map, DoorLabel, root.id);
    var camera = await Node(s.Map, CameraLabel, root.id);
    var figure = await Node(s.Map, BoxLabel, root.id);
    var plain = await Node(s.Map, NoteLabel, root.id);
    var replica = await Node(s.Map, "camera picture", root.id, ownerId: camera.id);

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
      Caller = Caller(AliceName, AdminRole),
    }, new TestContext());

    string KindOf(BaseMarkerDTO node) =>
      response.Children.Single(c => c.Id == node.id).NodeKind;

    Assert.Equal("space", KindOf(space));
    Assert.Equal("diagram", KindOf(diagram));
    Assert.Equal("landmark", KindOf(landmark));
    Assert.Equal(CameraKind, KindOf(camera));
    Assert.Equal(FigureKind, KindOf(figure));
    Assert.Equal("object", KindOf(plain));
    // A replica answers with its owner's kind: the picture of a camera is still that camera.
    Assert.Equal(CameraKind, KindOf(replica));
  }

  [Fact]
  public async Task GetByIdsReturnsTheVisualAndKindOfAFigure()
  {
    var s = Create();
    var root = await Node(s.Map, RootLabel);
    var figure = await Node(s.Map, BoxLabel, root.id);
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
      color = FigureColor,
      text = VisualText,
      rotation_deg = 45,
    });
    await s.Db.SaveChangesAsync();

    var figures = await s.Grpc.GetByIds(new MapProto.GetByIdsRequest
    {
      Ids = { figure.id! },
      Caller = Caller(AliceName, AdminRole),
    }, new TestContext());

    var fig = Assert.Single(figures.Figs);
    Assert.Equal(FigureKind, fig.NodeKind);
    Assert.Equal(FigureColor, fig.Visual.Color);
    Assert.Equal(VisualText, fig.Visual.Text);
    Assert.True(fig.Visual.HasRotationDeg);
    Assert.Equal(45, fig.Visual.RotationDeg);
  }

  [Fact]
  public async Task GetMarkersByIdsReportsAPartsPresenceThroughItsReplica()
  {
    var s = Create();
    var root = await Node(s.Map, RootLabel);
    var plain = await Node(s.Map, NoteLabel, root.id);
    var camera = await Node(s.Map, CameraLabel, root.id);
    var part = await Node(s.Map, DoorLabel, camera.id);
    var replica = await Node(s.Map, "door picture", root.id, ownerId: part.id);

    s.Db.DeviceNodes.Add(TestCameras.DeviceNode(TestCameras.One, Guid.Parse(camera.id!)));
    s.Db.DeviceNodes.Add(new DBDeviceNode
    {
      object_id = Guid.Parse(part.id!),
      source_kind = DeviceObjectId.CameraSource,
      source_id = TestCameras.One,
      part_kind = DeviceObjectId.DigitalInputPart,
      part_token = "1",
      present = false,
    });
    await s.Db.SaveChangesAsync();

    var answer = await s.Grpc.GetMarkersByIds(new MapProto.GetByIdsRequest
    {
      Ids = { plain.id!, part.id!, replica.id! },
      Caller = Caller(AliceName, AdminRole),
    }, new TestContext());

    var byId = answer.Markers.ToDictionary(m => m.Id);
    Assert.True(byId[part.id!].HasPresent);
    Assert.False(byId[part.id!].Present);
    // A picture answers with its owner's part, as it answers with its owner's camera and kind.
    Assert.True(byId[replica.id!].HasPresent);
    Assert.False(byId[replica.id!].Present);
    // A node with no device behind it says nothing, rather than "absent".
    Assert.False(byId[plain.id!].HasPresent);
  }

  [Fact]
  public async Task GetByIdsReportsAPartsPresenceOnItsFigure()
  {
    var s = Create();
    var root = await Node(s.Map, RootLabel);
    var camera = await Node(s.Map, CameraLabel, root.id);
    var part = await Node(s.Map, DoorLabel, camera.id);
    var guid = Guid.Parse(part.id!);

    s.Db.DeviceNodes.Add(TestCameras.DeviceNode(TestCameras.One, Guid.Parse(camera.id!)));
    s.Db.DeviceNodes.Add(new DBDeviceNode
    {
      object_id = guid,
      source_kind = DeviceObjectId.CameraSource,
      source_id = TestCameras.One,
      part_kind = DeviceObjectId.DigitalInputPart,
      part_token = "1",
      present = false,
    });
    s.Db.GeoObjects.Add(new DBGeoObject
    {
      id = guid,
      figure = new NetTopologySuite.Geometries.Point(0, 0),
    });
    await s.Db.SaveChangesAsync();

    var figures = await s.Grpc.GetByIds(new MapProto.GetByIdsRequest
    {
      Ids = { part.id! },
      Caller = Caller(AliceName, AdminRole),
    }, new TestContext());

    var fig = Assert.Single(figures.Figs);
    Assert.True(fig.HasPresent);
    Assert.False(fig.Present);
  }

  // ── Alarms ──────────────────────────────────────────────────────────────────────────────────

  private static async Task<string> AlarmCameraAsync(Services s, params string[] actions)
  {
    var node = await Node(s.Map, CameraNamePrefix + Guid.NewGuid().ToString(CompactGuidFormat)[..6]);
    var id = Guid.Parse(node.id!);
    s.Db.DeviceNodes.Add(new DBDeviceNode
    {
      object_id = id,
      source_kind = DeviceObjectId.CameraSource,
      source_id = Guid.NewGuid().ToString(),
      part_kind = DeviceObjectId.DevicePart,
      part_token = string.Empty,
    });
    foreach (var action in actions)
    {
      s.Db.Rights.Add(new DBRight
      {
        object_id = id,
        subject = AliceName,
        action = action,
      });
    }

    await s.Db.SaveChangesAsync();
    return node.id!;
  }

  [Fact]
  public async Task GetAlarmsFiltersByViewAndSaysWhoMayAcknowledge()
  {
    var s = Create();
    var node = await AlarmCameraAsync(s, RightActions.View, RightActions.Acknowledge);
    var objectId = Guid.Parse(node);

    await s.Alarms.RaiseAsync(new RaiseAlarmSpec(
      objectId, AlarmSource.Sensor, SensorId), default);

    var pages = await s.Grpc.GetAlarms(new MapProto.GetAlarmsRequest
    {
      Caller = Caller(AliceName),
    }, new TestContext());

    var alarm = Assert.Single(pages.Open.Alarms);
    Assert.Equal(node, alarm.ObjectId);
    Assert.True(alarm.CanAck);
    Assert.StartsWith(CameraNamePrefix, alarm.ObjectName);

    // Armed: the acknowledgement stays available, but it asks for a comment. The camera's own
    // mode lives in the map.
    s.Arming.Armed = true;
    await TestWiring.ObjectSettings(s.Db).SetModeAsync(node, ArmMode.Armed, default);
    pages = await s.Grpc.GetAlarms(new MapProto.GetAlarmsRequest
    {
      Caller = Caller(AliceName),
    }, new TestContext());
    var armed = Assert.Single(pages.Open.Alarms);
    Assert.True(armed.CanAck);
    Assert.True(armed.AckRequiresComment);
    Assert.Equal(string.Empty, armed.AckBlockedReason);

    // No view right at all: the row never leaves the service.
    var other = await AlarmCameraAsync(s, RightActions.View);
    await s.Alarms.RaiseAsync(new RaiseAlarmSpec(
      Guid.Parse(other), AlarmSource.Sensor, "sensor:2"), default);

    var anonymous = await s.Grpc.GetAlarms(new MapProto.GetAlarmsRequest
    {
      Caller = Caller(BobName),
    }, new TestContext());
    Assert.Empty(anonymous.Open.Alarms);

    var admin = await s.Grpc.GetAlarms(new MapProto.GetAlarmsRequest
    {
      Caller = Caller(BobName, AdminRole),
    }, new TestContext());
    Assert.Equal(2, admin.Open.Alarms.Count);
  }

  [Fact]
  public async Task AcknowledgeAndCloseTravelThroughTheTransitions()
  {
    var s = Create();
    var node = await AlarmCameraAsync(s, RightActions.View, RightActions.Acknowledge);
    var objectId = Guid.Parse(node);

    await s.Alarms.RaiseAsync(new RaiseAlarmSpec(
      objectId, AlarmSource.Sensor, SensorId), default);
    var raised = (await s.Grpc.GetAlarms(new MapProto.GetAlarmsRequest
    {
      Caller = Caller(AliceName),
    }, new TestContext())).Open.Alarms.Single();

    // Disarmed, so the live condition may be acknowledged.
    var acknowledged = await s.Grpc.AcknowledgeAlarm(new MapProto.AcknowledgeAlarmRequest
    {
      Id = raised.Id,
      Comment = "seen",
      Caller = Caller(AliceName),
    }, new TestContext());
    Assert.Equal("acknowledged", acknowledged.Status);
    Assert.True(acknowledged.ConditionActive);

    // The condition goes away; the acknowledged alarm closes as returned_to_normal.
    await s.Alarms.NormalAsync(objectId, SensorId, default);

    var closed = (await s.Grpc.GetAlarms(new MapProto.GetAlarmsRequest
    {
      Caller = Caller(AliceName),
    }, new TestContext())).Closed.Alarms.Single();
    Assert.Equal(AlarmCloseReason.ReturnedToNormal, closed.CloseReason);
    Assert.Equal(AliceName, closed.ClosedBy);
  }

  [Fact]
  public async Task AcknowledgingWithoutTheRightIsRefused()
  {
    var s = Create();
    var node = await AlarmCameraAsync(s, RightActions.View);
    await s.Alarms.RaiseAsync(new RaiseAlarmSpec(
      Guid.Parse(node), AlarmSource.Sensor, SensorId), default);

    var raised = (await s.Grpc.GetAlarms(new MapProto.GetAlarmsRequest
    {
      Caller = Caller(AliceName),
    }, new TestContext())).Open.Alarms.Single();
    Assert.False(raised.CanAck);
    Assert.Equal("no_right", raised.AckBlockedReason);

    var error = await Assert.ThrowsAsync<RpcException>(() => s.Grpc.AcknowledgeAlarm(
      new MapProto.AcknowledgeAlarmRequest { Id = raised.Id, Caller = Caller(AliceName) },
      new TestContext()));
    Assert.Equal(StatusCode.PermissionDenied, error.StatusCode);
  }

  // A device's canonical node hangs under its camera's server, never under the space the camera
  // stands in: a zone reaches it through the camera's reference replica, not through the tree.
  [Fact]
  public async Task AcceptingAZoneReachesTheDevicesThatStandInIt()
  {
    var s = Create();
    var (space, _, part) = await ZoneWithSensorAsync(s);
    await s.Alarms.RaiseAsync(new RaiseAlarmSpec(part, AlarmSource.Sensor, SensorId), default);

    var result = await s.Grpc.AcknowledgeSubtreeAlarms(
      new MapProto.AcknowledgeSubtreeAlarmsRequest
      {
        ObjectId = space.ToString(),
        Caller = Caller(AdminRole, AdminRole),
      },
      new TestContext());

    Assert.Equal(1, result.Acknowledged);
    Assert.Empty(result.Rejected);
  }

  [Fact]
  public async Task AcceptingAZoneOnGuardRequiresAComment()
  {
    var s = Create();
    var (space, _, part) = await ZoneWithSensorAsync(s);
    s.Arming.Armed = true;
    await s.Alarms.RaiseAsync(new RaiseAlarmSpec(part, AlarmSource.Sensor, SensorId), default);

    var withoutComment = await s.Grpc.AcknowledgeSubtreeAlarms(
      new MapProto.AcknowledgeSubtreeAlarmsRequest
      {
        ObjectId = space.ToString(),
        Caller = Caller(AdminRole, AdminRole),
      },
      new TestContext());

    Assert.Equal(0, withoutComment.Acknowledged);
    Assert.Equal("comment_required", Assert.Single(withoutComment.Rejected).Reason);

    var withComment = await s.Grpc.AcknowledgeSubtreeAlarms(
      new MapProto.AcknowledgeSubtreeAlarmsRequest
      {
        ObjectId = space.ToString(),
        Comment = AckComment,
        Caller = Caller(AdminRole, AdminRole),
      },
      new TestContext());

    Assert.Equal(1, withComment.Acknowledged);
    Assert.Empty(withComment.Rejected);
  }

  [Fact]
  public async Task ABypassedDevicesAlarmIsGroupedUnderItsZone()
  {
    var s = Create();
    var (space, _, part) = await ZoneWithSensorAsync(s);
    s.Db.ObjectArming.Add(new DBObjectArming { object_id = space, armed = true });
    await TestWiring.ObjectSettings(s.Db).SetModeAsync(
      part.ToString(), ArmMode.Disarmed, default);
    await s.Db.SaveChangesAsync();
    await s.Alarms.RaiseAsync(new RaiseAlarmSpec(part, AlarmSource.Sensor, SensorId), default);

    var alarm = Assert.Single((await s.Grpc.GetAlarms(new MapProto.GetAlarmsRequest
    {
      Caller = Caller(AdminRole, AdminRole),
    }, new TestContext())).Open.Alarms);

    Assert.Equal(space.ToString(), alarm.ArmingSourceObjectId);

    // The zone goes off guard: the device is no longer excluded from a guarded zone, so its
    // answer falls back to the unguarded bucket. The alarm itself is untouched.
    var arming = await s.Db.ObjectArming.SingleAsync(a => a.object_id == space);
    arming.armed = false;
    await s.Db.SaveChangesAsync();

    var off = Assert.Single((await s.Grpc.GetAlarms(new MapProto.GetAlarmsRequest
    {
      Caller = Caller(AdminRole, AdminRole),
    }, new TestContext())).Open.Alarms);

    Assert.Equal(string.Empty, off.ArmingSourceObjectId);
  }

  /// <summary>A space with a camera's reference replica in it and a digital input under the camera.</summary>
  private static async Task<(Guid Space, Guid Camera, Guid Part)> ZoneWithSensorAsync(Services s)
  {
    var space = Guid.NewGuid();
    await s.Map.UpdateHierarchyAsync([new BaseMarkerDTO { id = space.ToString(), name = FloorLabel }]);
    s.Db.Spaces.Add(new DBSpace { object_id = space, kind = SpaceKinds.Map });

    var cameraNode = Guid.NewGuid();
    var part = Guid.NewGuid();
    var cameraId = Guid.NewGuid().ToString();
    await s.Map.UpdateHierarchyAsync([
      new BaseMarkerDTO { id = cameraNode.ToString(), name = CameraLabel },
      new BaseMarkerDTO { id = part.ToString(), parent_id = cameraNode.ToString(), name = DoorLabel },
    ]);
    s.Db.DeviceNodes.Add(new DBDeviceNode
    {
      object_id = cameraNode, source_kind = DeviceObjectId.CameraSource,
      source_id = cameraId, part_kind = DeviceObjectId.DevicePart, part_token = string.Empty,
    });
    s.Db.DeviceNodes.Add(new DBDeviceNode
    {
      object_id = part, source_kind = DeviceObjectId.CameraSource,
      source_id = cameraId, part_kind = DeviceObjectId.DigitalInputPart, part_token = "1",
    });

    var replica = Guid.NewGuid();
    await s.Map.UpdateHierarchyAsync([new BaseMarkerDTO
    {
      id = replica.ToString(), parent_id = space.ToString(),
      owner_id = cameraNode.ToString(), name = "camera on the plan",
    }]);
    s.Db.CameraPlacements.Add(new DBCameraPlacement
    {
      object_id = replica, camera_id = cameraId, is_reference = true,
    });
    await s.Db.SaveChangesAsync();

    return (space, cameraNode, part);
  }

  [Fact]
  public async Task APropertyWriteDoesNotMoveTheNode()
  {
    var s = Create();
    var building = await Node(s.Map, BuildingLabel);
    var floor = await Node(s.Map, FloorLabel, building.id);

    // The property DTO carries no parent; before, the hierarchy write copied that null onto the
    // row and every property update silently moved the node to the root.
    await s.Updates.UpdateProperties(new ObjPropsDTO
    {
      id = floor.id,
      extra_props = [new ObjExtraPropertyDTO { prop_name = NotePropName, str_val = "kept" }],
    });

    var stored = await s.Db.Markers.AsNoTracking()
      .SingleAsync(m => m.id == Guid.Parse(floor.id!));
    Assert.Equal(Guid.Parse(building.id!), stored.parent_id);
    Assert.Equal(FloorLabel, stored.name);
  }

  [Fact]
  public async Task GetAlarmAnswersTheRowThePageHas()
  {
    var s = Create();
    var node = await AlarmCameraAsync(s, RightActions.View, RightActions.Acknowledge);
    var raised = await s.Alarms.RaiseAsync(new RaiseAlarmSpec(
      Guid.Parse(node), AlarmSource.Sensor, SensorId), default);

    var alarm = await s.Grpc.GetAlarm(new MapProto.GetAlarmRequest
    {
      Id = raised.Alarm!.id.ToString(),
      Caller = Caller(AliceName),
    }, new TestContext());

    Assert.Equal(node, alarm.ObjectId);
    Assert.True(alarm.CanAck);
    Assert.False(alarm.DeviceAbsent);
    Assert.Equal(string.Empty, alarm.AckBlockedReason);
  }

  [Fact]
  public async Task AGonePartIsReportedAbsentAndClosesOnlyWithAComment()
  {
    var s = Create();
    var (_, _, part) = await ZoneWithSensorAsync(s);
    var raised = await s.Alarms.RaiseAsync(
      new RaiseAlarmSpec(part, AlarmSource.Sensor, SensorId), default);

    var node = await s.Db.DeviceNodes.SingleAsync(n => n.object_id == part);
    node.present = false;
    await s.Db.SaveChangesAsync();

    var alarm = await s.Grpc.GetAlarm(new MapProto.GetAlarmRequest
    {
      Id = raised.Alarm!.id.ToString(),
      Caller = Caller(AdminRole, AdminRole),
    }, new TestContext());
    Assert.True(alarm.DeviceAbsent);
    Assert.True(alarm.CanAck);

    var missingComment = await Assert.ThrowsAsync<RpcException>(() => s.Grpc.CloseAlarm(
      new MapProto.CloseAlarmRequest
      {
        Id = alarm.Id,
        Reason = AlarmCloseReason.Other,
        Caller = Caller(AdminRole, AdminRole),
      },
      new TestContext()));
    Assert.Equal(StatusCode.FailedPrecondition, missingComment.StatusCode);

    var closed = await s.Grpc.CloseAlarm(
      new MapProto.CloseAlarmRequest
      {
        Id = alarm.Id,
        Reason = AlarmCloseReason.Other,
        Comment = AckComment,
        Caller = Caller(AdminRole, AdminRole),
      },
      new TestContext());
    Assert.Equal(AlarmStatus.Closed, closed.Status);
  }

  // ── A fault's API ─────────────────────────────────────────────────────────────────────────────

  private static async Task<(string Camera, Guid Part)> CameraWithPartAsync(
    Services s, params string[] actions)
  {
    var camera = await AlarmCameraAsync(s, actions);
    var part = await Node(s.Map, DoorLabel, camera);
    var partId = Guid.Parse(part.id!);
    s.Db.DeviceNodes.Add(new DBDeviceNode
    {
      object_id = partId,
      source_kind = DeviceObjectId.CameraSource,
      source_id = TestCameras.One,
      part_kind = DeviceObjectId.DigitalInputPart,
      part_token = "1",
    });
    await s.Db.SaveChangesAsync();
    return (camera, partId);
  }

  [Fact]
  public async Task AFaultIsAcknowledgedAndResetButAConditionAlarmIsNot()
  {
    var s = Create();
    var (camera, part) = await CameraWithPartAsync(
      s, RightActions.View, RightActions.Acknowledge);
    var key = DeviceFaultService.DedupKey(part);

    await s.Alarms.RaiseAsync(new RaiseAlarmSpec(
      part, AlarmSource.Fault, key, Severity: AlarmSeverity.Warning,
      Trigger: DeviceFaultService.PartAbsentTrigger), default);

    var active = (await s.Grpc.GetAlarms(new MapProto.GetAlarmsRequest
    {
      Caller = Caller(AliceName),
    }, new TestContext())).Open.Alarms.Single();
    Assert.True(active.CanAck);
    Assert.False(active.AckRequiresComment);
    Assert.Equal(string.Empty, active.AckBlockedReason);

    await s.Alarms.NormalFaultAsync(part, key, default);

    var returned = await s.Grpc.GetAlarm(new MapProto.GetAlarmRequest
    {
      Id = active.Id,
      Caller = Caller(AliceName),
    }, new TestContext());
    Assert.True(returned.CanAck); // the right, not the acknowledge action
    Assert.Equal("reset_required", returned.AckBlockedReason);
    Assert.False(returned.AckRequiresComment);

    var acknowledged = await Assert.ThrowsAsync<RpcException>(() => s.Grpc.AcknowledgeAlarm(
      new MapProto.AcknowledgeAlarmRequest { Id = active.Id, Caller = Caller(AliceName) },
      new TestContext()));
    Assert.Equal(StatusCode.FailedPrecondition, acknowledged.StatusCode);

    var wrongReason = await Assert.ThrowsAsync<RpcException>(() => s.Grpc.CloseAlarm(
      new MapProto.CloseAlarmRequest
      {
        Id = active.Id,
        Reason = AlarmCloseReason.Other,
        Comment = AckComment,
        Caller = Caller(AliceName),
      },
      new TestContext()));
    Assert.Equal(StatusCode.InvalidArgument, wrongReason.StatusCode);

    var missingComment = await Assert.ThrowsAsync<RpcException>(() => s.Grpc.CloseAlarm(
      new MapProto.CloseAlarmRequest
      {
        Id = active.Id,
        Reason = AlarmCloseReason.Reset,
        Caller = Caller(AliceName),
      },
      new TestContext()));
    Assert.Equal(StatusCode.FailedPrecondition, missingComment.StatusCode);

    var reset = await s.Grpc.CloseAlarm(new MapProto.CloseAlarmRequest
    {
      Id = active.Id,
      Reason = AlarmCloseReason.Reset,
      Comment = AckComment,
      Caller = Caller(AliceName),
    }, new TestContext());
    Assert.Equal(AlarmStatus.Closed, reset.Status);
    Assert.Equal(AlarmCloseReason.Reset, reset.CloseReason);

    // A condition alarm keeps its own close reasons: reset is not one of them.
    var sensor = await s.Alarms.RaiseAsync(new RaiseAlarmSpec(
      Guid.Parse(camera), AlarmSource.Sensor, SensorId), default);
    var notAReset = await Assert.ThrowsAsync<RpcException>(() => s.Grpc.CloseAlarm(
      new MapProto.CloseAlarmRequest
      {
        Id = sensor.Alarm!.id.ToString(),
        Reason = AlarmCloseReason.Reset,
        Comment = AckComment,
        Caller = Caller(AliceName),
      },
      new TestContext()));
    Assert.Equal(StatusCode.InvalidArgument, notAReset.StatusCode);
  }

  [Fact]
  public async Task AcceptingAZoneLeavesAFaultAlone()
  {
    var s = Create();
    var (camera, part) = await CameraWithPartAsync(
      s, RightActions.View, RightActions.Acknowledge);
    var key = DeviceFaultService.DedupKey(part);

    await s.Alarms.RaiseAsync(new RaiseAlarmSpec(
      part, AlarmSource.Fault, key, Severity: AlarmSeverity.Warning,
      Trigger: DeviceFaultService.PartAbsentTrigger), default);

    var result = await s.Grpc.AcknowledgeSubtreeAlarms(
      new MapProto.AcknowledgeSubtreeAlarmsRequest
      {
        ObjectId = camera,
        Comment = AckComment,
        Caller = Caller(AliceName),
      },
      new TestContext());

    Assert.Equal(0, result.Acknowledged);
    Assert.Empty(result.Rejected);

    var row = await s.Db.Alarms.AsNoTracking()
      .SingleAsync(a => a.source == AlarmSource.Fault);
    Assert.Equal(AlarmStatus.Active, row.status);
  }

  [Fact]
  public async Task UpdateDiagramsWritesTheFirstRowOfANewDiagram()
  {
    var s = Create();
    var note = await Node(s.Map, NoteLabel);

    // A node with no diagram row yet — the editor's "make this a diagram" — is not a
    // representation of anything, and the request is the one that gives it its row.
    var written = await s.Grpc.UpdateDiagrams(new MapProto.DiagramList
    {
      Diagrams =
      {
        new MapProto.Diagram
        {
          Id = note.id!,
          DgrType = Guid.NewGuid().ToString(),
          Geometry = new MapProto.DiagramCoordinate { Width = PaperWidth, Height = PaperHeight },
        },
      },
    }, new TestContext());

    var row = Assert.Single(written.Diagrams);
    Assert.Equal(note.id, row.Id);
    Assert.False(string.IsNullOrEmpty(row.DgrType));
    Assert.Equal(1, await s.Db.Diagrams.CountAsync());
  }

  [Fact]
  public async Task UpdateDiagramsRefusesARegionItsParentsTypeDoesNotOffer()
  {
    var s = Create();
    var rack = await Node(s.Map, RackLabel);
    var computer = await Node(s.Map, "switch", rack.id);

    // The rack has no template, so it offers no regions: the key would be a place nothing draws.
    var error = await Assert.ThrowsAsync<RpcException>(() => s.Grpc.UpdateDiagrams(
      new MapProto.DiagramList
      {
        Diagrams = { new MapProto.Diagram { Id = computer.id!, RegionId = FirstRegionKey } },
      }, new TestContext()));

    Assert.Equal(StatusCode.InvalidArgument, error.StatusCode);
    Assert.Empty(await s.Db.Diagrams.ToListAsync());
  }

  [Fact]
  public async Task AttachDeviceDiagramRefusesARequestThatNamesTwoPlaces()
  {
    var s = Create();
    const string CameraId = "camera-1";
    var geometry = new MapProto.DiagramCoordinate { Width = 10, Height = 10 };
    var at = new MapProto.Coordinate { Lat = 1, Lon = 2 };

    static async Task Refused(Services s, MapProto.AttachDeviceDiagramRequest request)
    {
      var error = await Assert.ThrowsAsync<RpcException>(
        () => s.Grpc.AttachDeviceDiagram(request, new TestContext()));
      Assert.Equal(StatusCode.InvalidArgument, error.StatusCode);
    }

    // A space and a diagram's canvas are two different places, and so are a region, a rectangle
    // and a point: the server refuses the combination instead of picking one.
    await Refused(s, new MapProto.AttachDeviceDiagramRequest
    {
      SourceId = CameraId, SpaceId = "space-1", CanvasId = "canvas-1",
    });
    await Refused(s, new MapProto.AttachDeviceDiagramRequest
    {
      SourceId = CameraId, CanvasId = "canvas-1", RegionId = FirstRegionKey, Geometry = geometry,
    });
    await Refused(s, new MapProto.AttachDeviceDiagramRequest
    {
      SourceId = CameraId, RegionId = FirstRegionKey,
    });
    await Refused(s, new MapProto.AttachDeviceDiagramRequest
    {
      SourceId = CameraId, CanvasId = "canvas-1", At = at,
    });

    Assert.Empty(await s.Db.DeviceDiagrams.ToListAsync());
  }
}
