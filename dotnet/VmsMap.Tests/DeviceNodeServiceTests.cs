using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using VmsMap.Adapter;
using VmsMap.Clients;
using VmsMap.Db;
using VmsMap.Domain;

namespace VmsMap.Tests;

// The camera half of the adapter on the in-memory provider: the canonical node, the server node,
// the device_node reverse index, deletion of a camera VmsCfg no longer knows, and the armed
// property merge.
public class DeviceNodeServiceTests
{
  private sealed record Services(VmsMapDbContext Db, MapDb Map, DeviceNodeService Nodes);

  private static Services Create()
  {
    var options = new DbContextOptionsBuilder<VmsMapDbContext>()
      .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
      .Options;

    var db = new VmsMapDbContext(options);
    var map = new MapDb(db);
    var states = new StatesUpdateService(new StateService(db), map, db);
    var levels = new LevelService(db);
    var tracks = new TracksUpdateService(map, new TrackService(db, levels), new GeoService(db, levels));
    var updates = new MapUpdateService(map, tracks, states);

    return new Services(db, map,
      new DeviceNodeService(db, map, updates, NullLogger<DeviceNodeService>.Instance));
  }

  private static async Task<BaseMarkerDTO> Node(
    MapDb map, string name, string? parentId = null, string? ownerId = null)
  {
    var node = new BaseMarkerDTO { name = name, parent_id = parentId, owner_id = ownerId };
    await map.UpdateHierarchyAsync([node]);
    return node;
  }

  private static Guid ServerGuid(string serverId) =>
    Guid.Parse(ObjectIdGenerator.GenerateObjectId(serverId, "server"));

  [Fact]
  public async Task AnUnplacedCameraHangsUnderItsServerNode()
  {
    var s = Create();

    await s.Nodes.EnsureCameraAsync(new CameraInfo(TestCameras.One, "Front door", "srv1"), default);

    var canonical = Guid.Parse(CameraObjectId.For(TestCameras.One));
    var marker = await s.Db.Markers.SingleAsync(m => m.id == canonical);
    Assert.Equal("Front door", marker.name);
    Assert.Equal(ServerGuid("srv1"), marker.parent_id);

    var server = await s.Db.Markers.SingleAsync(m => m.id == ServerGuid("srv1"));
    Assert.Equal("srv1", server.name);
    Assert.Null(server.parent_id);

    var index = await s.Db.DeviceNodes.SingleAsync();
    Assert.Equal(canonical, index.object_id);
    Assert.Equal(TestCameras.One, index.source_id);
  }

  [Fact]
  public async Task APlacedCameraStaysUnderItsServerAndOnlyItsNameMoves()
  {
    var s = Create();
    var space = await Node(s.Map, "Building");
    var canonicalId = Guid.Parse(CameraObjectId.For(TestCameras.One));
    // A stale parent, as a database written under the old model would have it.
    await s.Map.UpdateHierarchyAsync([
      new BaseMarkerDTO { id = canonicalId.ToString(), name = "Old name", parent_id = space.id }
    ]);

    await s.Nodes.EnsureCameraAsync(new CameraInfo(TestCameras.One, "Front door", "srv1"), default);

    var marker = await s.Db.Markers.SingleAsync(m => m.id == canonicalId);
    Assert.Equal("Front door", marker.name);
    Assert.Equal(ServerGuid("srv1"), marker.parent_id);
    Assert.Equal(canonicalId, (await s.Db.DeviceNodes.SingleAsync()).object_id);
  }

  [Fact]
  public async Task AReplicaInAContainerKeepsItsParentWhileTheCanonicalNodeStaysUnderTheServer()
  {
    var s = Create();
    var space = await Node(s.Map, "Building");
    var container = await Node(s.Map, "Car park", space.id);
    var canonicalId = Guid.Parse(CameraObjectId.For(TestCameras.One));

    // The camera is drawn in a container on the building's canvas; that is a replica of it.
    var replicaId = Guid.NewGuid();
    await s.Map.UpdateHierarchyAsync([
      new BaseMarkerDTO {
        id = replicaId.ToString(), name = "Front door", parent_id = container.id, owner_id = canonicalId.ToString() }
    ]);
    s.Db.CameraPlacements.Add(new DBCameraPlacement
    {
      object_id = replicaId,
      camera_id = TestCameras.One,
      is_reference = true,
      height_m = 3,
      hfov_deg = 60,
      range_m = 30,
    });
    await s.Db.SaveChangesAsync();

    // The periodic reconcile runs for every camera: the canonical node goes under the server, and
    // the replica keeps the parent the administrator gave it.
    await s.Nodes.EnsureCameraAsync(new CameraInfo(TestCameras.One, "Front door", "srv1"), default);

    Assert.Equal(ServerGuid("srv1"), (await s.Db.Markers.SingleAsync(m => m.id == canonicalId)).parent_id);
    Assert.Equal(Guid.Parse(container.id!), (await s.Db.Markers.SingleAsync(m => m.id == replicaId)).parent_id);
  }

  [Fact]
  public async Task AReferencePlacementRemovedElsewhereReturnsTheCameraToItsServer()
  {
    var s = Create();
    var canonicalId = Guid.Parse(CameraObjectId.For(TestCameras.One));
    await Node(s.Map, "Front door");

    await s.Nodes.EnsureCameraAsync(new CameraInfo(TestCameras.One, "Front door", "srv1"), default);

    var marker = await s.Db.Markers.SingleAsync(m => m.id == canonicalId);
    Assert.Equal(ServerGuid("srv1"), marker.parent_id);
  }

  [Fact]
  public async Task RemovingACameraDeletesItsPicturesPlacementsAndObservations()
  {
    var s = Create();
    var canonicalId = Guid.Parse(CameraObjectId.For(TestCameras.One));
    await s.Nodes.EnsureCameraAsync(new CameraInfo(TestCameras.One, "Front door", "srv1"), default);

    await Node(s.Map, "Front door", ownerId: CameraObjectId.For(TestCameras.One));
    var replicaMarker = await s.Db.Markers.SingleAsync(m => m.owner_id == canonicalId);
    var replica = replicaMarker.id;

    s.Db.CameraPlacements.Add(new DBCameraPlacement
    {
      object_id = canonicalId,
      camera_id = TestCameras.One,
      is_reference = true,
      height_m = 3,
      hfov_deg = 60,
      range_m = 30,
    });
    s.Db.CameraPlacements.Add(new DBCameraPlacement
    {
      object_id = replica,
      camera_id = TestCameras.One,
      is_reference = false,
      height_m = 3,
      hfov_deg = 60,
      range_m = 30,
    });
    s.Db.PtzGeoObservations.Add(new DBPtzGeoObservation
    {
      camera_id = TestCameras.One,
      landmark_id = Guid.NewGuid(),
      pan = 1,
      tilt = 2,
    });
    s.Db.GroundObservations.Add(new DBGroundObservation
    {
      camera_id = TestCameras.One,
      landmark_id = Guid.NewGuid(),
      x = 0.5f,
      y = 0.5f,
    });
    await s.Db.SaveChangesAsync();

    await s.Nodes.RemoveCameraAsync(TestCameras.One, default);

    Assert.False(await s.Db.Markers.AnyAsync(m => m.id == canonicalId || m.id == replica));
    Assert.Empty(await s.Db.CameraPlacements.ToListAsync());
    Assert.Empty(await s.Db.PtzGeoObservations.ToListAsync());
    Assert.Empty(await s.Db.GroundObservations.ToListAsync());
    Assert.Empty(await s.Db.DeviceNodes.ToListAsync());
  }

  [Fact]
  public async Task AResyncRemovesCamerasVmsCfgNoLongerLists()
  {
    var s = Create();
    await s.Nodes.EnsureCameraAsync(new CameraInfo(TestCameras.One, "One", "srv1"), default);
    await s.Nodes.EnsureCameraAsync(new CameraInfo(TestCameras.Two, "Two", "srv1"), default);

    await s.Nodes.RemoveUnknownAsync(
      new HashSet<string>(StringComparer.Ordinal) { TestCameras.Two }, default);

    Assert.False(await s.Db.Markers.AnyAsync(
      m => m.id == Guid.Parse(CameraObjectId.For(TestCameras.One))));
    Assert.True(await s.Db.Markers.AnyAsync(
      m => m.id == Guid.Parse(CameraObjectId.For(TestCameras.Two))));
    Assert.Equal(TestCameras.Two, (await s.Db.DeviceNodes.SingleAsync()).source_id);
  }

  [Fact]
  public async Task TheArmedPropertyMergesWithTheOthers()
  {
    var s = Create();
    await s.Nodes.EnsureCameraAsync(new CameraInfo(TestCameras.One, "Front door", "srv1"), default);
    var canonicalId = CameraObjectId.For(TestCameras.One);

    await ((IMapService)s.Map).UpdatePropNotDeleteAsync([
      new ObjPropsDTO
      {
        id = canonicalId,
        extra_props = [new ObjExtraPropertyDTO { prop_name = "profile", str_val = "main" }],
      }
    ]);

    await s.Nodes.SetPropertyAsync(TestCameras.One, "armed", "True", default);

    var props = await ((IMapService)s.Map).GetPropAsync(canonicalId);
    Assert.Equal(2, props.extra_props!.Count);
    Assert.Contains(props.extra_props, p => p.prop_name == "armed" && p.str_val == "True");
    Assert.Contains(props.extra_props, p => p.prop_name == "profile");
  }
}
