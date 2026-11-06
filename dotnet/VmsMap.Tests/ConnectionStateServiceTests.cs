using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using VmsMap.Adapter;
using VmsMap.Clients;
using VmsMap.Db;
using VmsMap.Domain;

namespace VmsMap.Tests;

// The connectivity half of the adapter: a path with no source means DISCONNECTED, a failed
// question changes nothing, and the periodic reconcile puts an unplaced camera back under its
// server without touching a placed one.
public class ConnectionStateServiceTests
{
  private sealed record Services(
    VmsMapDbContext Db, MapDb Map, CameraNodeService Nodes,
    ConnectionStateService Connections);

  private static Services Create()
  {
    var options = new DbContextOptionsBuilder<VmsMapDbContext>()
      .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
      .Options;

    var db = new VmsMapDbContext(options);
    var map = new MapDb(db);
    var stateService = new StateService(db);
    var statesUpdate = new StatesUpdateService(stateService, map, db);
    var writer = new CameraStateWriter(db, stateService, statesUpdate);
    var nodes = new CameraNodeService(db, map, new MapUpdateService(
      map,
      new TracksUpdateService(map, new TrackService(db, new LevelService(db)),
        new GeoService(db, new LevelService(db))),
      statesUpdate), NullLogger<CameraNodeService>.Instance);

    return new Services(db, map, nodes, new ConnectionStateService(writer, nodes));
  }

  private static async Task<string?> State(VmsMapDbContext db, string cameraId)
  {
    var id = Guid.Parse(CameraObjectId.For(cameraId));
    var row = await db.ObjectStates
      .Include(s => s.states)
      .FirstOrDefaultAsync(s => s.id == id);
    return row is null ? null : string.Join(",", row.states.Select(s => s.state).OrderBy(s => s));
  }

  private static CameraInfo Camera() =>
    new(TestCameras.One, "Front door", "srv1");

  [Fact]
  public async Task APathWithoutASourceMakesTheCameraDisconnectedAndBack()
  {
    var s = Create();
    await s.Nodes.EnsureCameraAsync(Camera(), default);

    await s.Connections.ApplyAsync(
      new Dictionary<string, bool> { [TestCameras.One] = false }, default);
    Assert.Equal(CameraStateWriter.Disconnected, await State(s.Db, TestCameras.One));

    await s.Connections.ApplyAsync(
      new Dictionary<string, bool> { [TestCameras.One] = true }, default);
    Assert.Equal(CameraStateWriter.Connected, await State(s.Db, TestCameras.One));
  }

  [Fact]
  public async Task AFailedLookupLeavesTheLastKnownState()
  {
    var s = Create();
    await s.Nodes.EnsureCameraAsync(Camera(), default);
    await s.Connections.ApplyAsync(
      new Dictionary<string, bool> { [TestCameras.One] = true }, default);

    // The source could not be asked: the camera is absent from the answer.
    await s.Connections.ApplyAsync(new Dictionary<string, bool>(), default);

    Assert.Equal(CameraStateWriter.Connected, await State(s.Db, TestCameras.One));
  }

  [Fact]
  public async Task ReconcileReturnsAnUnplacedCameraToItsServer()
  {
    var s = Create();
    var space = new BaseMarkerDTO { name = "Building" };
    await s.Map.UpdateHierarchyAsync([space]);

    // The canonical node was left somewhere by an older placement that no longer exists.
    var canonicalId = Guid.Parse(CameraObjectId.For(TestCameras.One));
    await s.Map.UpdateHierarchyAsync([
      new BaseMarkerDTO { id = canonicalId.ToString(), parent_id = space.id, name = "Front door" }
    ]);

    await s.Connections.ReconcileAsync([Camera()], default);

    var marker = await s.Db.Markers.SingleAsync(m => m.id == canonicalId);
    Assert.Equal(
      Guid.Parse(ObjectIdGenerator.GenerateObjectId("srv1", "server")), marker.parent_id);
  }

  [Fact]
  public async Task ReconcileLeavesAPlacedCameraWhereItStands()
  {
    var s = Create();
    var space = new BaseMarkerDTO { name = "Building" };
    await s.Map.UpdateHierarchyAsync([space]);
    var canonicalId = Guid.Parse(CameraObjectId.For(TestCameras.One));
    await s.Map.UpdateHierarchyAsync([
      new BaseMarkerDTO { id = canonicalId.ToString(), parent_id = space.id, name = "Front door" }
    ]);
    s.Db.CameraPlacements.Add(new DBCameraPlacement
    {
      object_id = canonicalId,
      camera_id = TestCameras.One,
      is_reference = true,
      height_m = 3,
      hfov_deg = 60,
      range_m = 30,
    });
    await s.Db.SaveChangesAsync();

    await s.Connections.ReconcileAsync([Camera()], default);

    var marker = await s.Db.Markers.SingleAsync(m => m.id == canonicalId);
    Assert.Equal(Guid.Parse(space.id!), marker.parent_id);
  }
}
