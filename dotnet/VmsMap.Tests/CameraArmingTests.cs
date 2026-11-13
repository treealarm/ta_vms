using Microsoft.EntityFrameworkCore;
using Vms.Map;
using VmsMap.Db;
using VmsMap.Domain;
using VmsMap.Domain.Arming;

namespace VmsMap.Tests;

/// <summary>
/// The map writes the flag a zone implies into the camera's VmsCfg state, because the services that
/// read that flag — VmsOnvif's retention gate, the routing gateway — cannot see the map. These
/// tests are about that write: when it happens, when it must not, and how the round of events ends.
/// </summary>
public class CameraArmingTests
{
  private const string CameraId = "94681efe-57da-5093-b420-1bed3b28dae8";

  private sealed record Fixture(
    VmsMapDbContext Db,
    MapDb Map,
    CameraArmingReconciler Reconciler,
    TestCameraState CameraState,
    TestWiring.TestCfgArmingWriter Cfg);

  private static async Task<Fixture> CreateAsync(string cameraMode = ArmMode.Inherit)
  {
    var options = new DbContextOptionsBuilder<VmsMapDbContext>()
      .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
      .Options;

    var db = new VmsMapDbContext(options);
    var map = new MapDb(db);
    var cameraState = new TestCameraState(false, cameraMode);
    var cfg = new TestWiring.TestCfgArmingWriter(cameraState);
    var reconciler = TestWiring.CameraArming(db, map, cameraState, cfg);

    return new Fixture(db, map, reconciler, cameraState, cfg);
  }

  private static async Task<string> NodeAsync(
    Fixture f, string name, string? parentId = null, string? ownerId = null)
  {
    var node = new BaseMarkerDTO
    {
      id = Guid.NewGuid().ToString(),
      name = name,
      parent_id = parentId,
      owner_id = ownerId,
    };
    await f.Map.UpdateHierarchyAsync([node]);
    return node.id!;
  }

  private static async Task<string> SpaceAsync(Fixture f, string name, bool armed)
  {
    var id = await NodeAsync(f, name);
    f.Db.Spaces.Add(new DBSpace { object_id = Guid.Parse(id), kind = SpaceKinds.Map });
    f.Db.ObjectArming.Add(new DBObjectArming { object_id = Guid.Parse(id), armed = armed });
    await f.Db.SaveChangesAsync();
    return id;
  }

  private static async Task PlaceReferenceAsync(Fixture f, string spaceId)
  {
    var replica = await NodeAsync(
      f, "Camera picture", parentId: spaceId, ownerId: CameraObjectId.For(CameraId));

    f.Db.CameraPlacements.Add(new DBCameraPlacement
    {
      object_id = Guid.Parse(replica),
      camera_id = CameraId,
      is_reference = true,
    });
    await f.Db.SaveChangesAsync();
  }

  [Fact]
  public async Task ACameraInAnArmedZoneIsWrittenOnGuard()
  {
    var f = await CreateAsync();
    var space = await SpaceAsync(f, "Floor", armed: true);
    await PlaceReferenceAsync(f, space);

    Assert.True(await f.Reconciler.ReconcileAsync(CameraId, default));
    Assert.True(f.CameraState.Armed);
    Assert.Contains((CameraId, true), f.Cfg.EffectiveFlags);
  }

  [Fact]
  public async Task ASecondLookWritesNothing()
  {
    var f = await CreateAsync();
    var space = await SpaceAsync(f, "Floor", armed: true);
    await PlaceReferenceAsync(f, space);

    await f.Reconciler.ReconcileAsync(CameraId, default);
    Assert.False(await f.Reconciler.ReconcileAsync(CameraId, default));
    Assert.Single(f.Cfg.EffectiveFlags);
  }

  [Fact]
  public async Task AZoneThatIsDisarmedTakesTheCameraOffGuard()
  {
    var f = await CreateAsync();
    var space = await SpaceAsync(f, "Floor", armed: false);
    await PlaceReferenceAsync(f, space);
    f.CameraState.Armed = true;

    Assert.True(await f.Reconciler.ReconcileAsync(CameraId, default));
    Assert.False(f.CameraState.Armed);
  }

  [Fact]
  public async Task ACameraWithItsOwnModeIsLeftAlone()
  {
    var f = await CreateAsync(cameraMode: ArmMode.Disarmed);
    var space = await SpaceAsync(f, "Floor", armed: true);
    await PlaceReferenceAsync(f, space);

    Assert.False(await f.Reconciler.ReconcileAsync(CameraId, default));
    Assert.Empty(f.Cfg.EffectiveFlags);
  }

  [Fact]
  public async Task ACameraThatStandsNowhereIsOff()
  {
    var f = await CreateAsync();
    f.CameraState.Armed = true;

    Assert.True(await f.Reconciler.ReconcileAsync(CameraId, default));
    Assert.False(f.CameraState.Armed);
  }

  [Fact]
  public async Task TheSweepReachesTheCamerasOfAnArmedZoneAndNoOthers()
  {
    var f = await CreateAsync();
    var armed = await SpaceAsync(f, "Floor", armed: true);
    await PlaceReferenceAsync(f, armed);

    Assert.True(await f.Reconciler.ReconcileAsync(CameraId, default));
    f.CameraState.Armed = false; // as if the world was left armed and the flag had not caught up
    await f.Reconciler.SweepAsync(default);

    Assert.True(f.CameraState.Armed);
  }
}
