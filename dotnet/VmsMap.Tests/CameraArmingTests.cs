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
  private const string OtherCameraId = "0a1b2c3d-4e5f-5091-a2b3-c4d5e6f70819";

  // The fixtures these tests share.
  private const string FloorName = "floor";
  private const string YardName = "yard";

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
    if (!string.Equals(cameraMode, ArmMode.Inherit, StringComparison.Ordinal))
    {
      await new DeviceObjectSettingsStore(db)
        .SetModeAsync(CameraObjectId.For(CameraId), cameraMode, default);
    }

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

  private static async Task PlaceCameraAsync(Fixture f, string cameraId, string spaceId)
  {
    // A picture names its original, and the original exists: the placement writer creates the
    // camera's canonical node before the replica that carries the point.
    await f.Map.UpdateHierarchyAsync([
      new BaseMarkerDTO { id = CameraObjectId.For(cameraId), name = "camera" }
    ]);

    var replica = await NodeAsync(
      f, "camera picture", parentId: spaceId, ownerId: CameraObjectId.For(cameraId));

    f.Db.CameraPlacements.Add(new DBCameraPlacement
    {
      object_id = Guid.Parse(replica),
      camera_id = cameraId,
      is_reference = true,
    });
    await f.Db.SaveChangesAsync();
  }

  private static Task PlaceReferenceAsync(Fixture f, string spaceId) =>
    PlaceCameraAsync(f, CameraId, spaceId);

  /// <summary>The camera's picture moves to another space: the same write a placement makes.</summary>
  private static async Task MoveReferenceAsync(Fixture f, string spaceId)
  {
    var replica = await f.Db.Markers.SingleAsync(
      m => m.owner_id == Guid.Parse(CameraObjectId.For(CameraId)));
    replica.parent_id = Guid.Parse(spaceId);
    await f.Db.SaveChangesAsync();
  }

  [Fact]
  public async Task ACameraInAnArmedZoneIsWrittenOnGuard()
  {
    var f = await CreateAsync();
    var space = await SpaceAsync(f, FloorName, armed: true);
    await PlaceReferenceAsync(f, space);

    Assert.True(await f.Reconciler.ReconcileAsync(CameraId, default));
    Assert.True(f.CameraState.Armed);
    Assert.Contains((CameraId, true), f.Cfg.CameraFlags);
  }

  [Fact]
  public async Task ASecondLookWritesNothing()
  {
    var f = await CreateAsync();
    var space = await SpaceAsync(f, FloorName, armed: true);
    await PlaceReferenceAsync(f, space);

    await f.Reconciler.ReconcileAsync(CameraId, default);
    Assert.False(await f.Reconciler.ReconcileAsync(CameraId, default));
    Assert.Single(f.Cfg.CameraFlags);
  }

  [Fact]
  public async Task AZoneThatIsDisarmedTakesTheCameraOffGuard()
  {
    var f = await CreateAsync();
    var space = await SpaceAsync(f, FloorName, armed: false);
    await PlaceReferenceAsync(f, space);
    f.CameraState.Armed = true;

    Assert.True(await f.Reconciler.ReconcileAsync(CameraId, default));
    Assert.False(f.CameraState.Armed);
  }

  [Fact]
  public async Task AnExplicitModeWinsOverTheZone()
  {
    var f = await CreateAsync(cameraMode: ArmMode.Disarmed);
    var space = await SpaceAsync(f, FloorName, armed: true);
    await PlaceReferenceAsync(f, space);
    f.CameraState.Armed = true; // as if the zone had put it on guard before the mode changed

    Assert.True(await f.Reconciler.ReconcileAsync(CameraId, default));
    Assert.False(f.CameraState.Armed);
    Assert.Contains((CameraId, false), f.Cfg.CameraFlags);
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
    var armed = await SpaceAsync(f, FloorName, armed: true);
    await PlaceReferenceAsync(f, armed);

    Assert.True(await f.Reconciler.ReconcileAsync(CameraId, default));
    f.CameraState.Armed = false; // as if the world was left armed and the flag had not caught up
    await f.Reconciler.SweepAsync(default);

    Assert.True(f.CameraState.Armed);
  }

  [Fact]
  public async Task AFlagWrittenFromAZoneThatMovedUnderneathIsCorrected()
  {
    var f = await CreateAsync();
    var armed = await SpaceAsync(f, FloorName, armed: true);
    var off = await SpaceAsync(f, YardName, armed: false);
    await PlaceReferenceAsync(f, armed);

    // The world moves the picture between the read of the zone and the write: the stale value
    // lands, and the same call has to see the new zone and settle on it.
    var moved = false;
    f.Cfg.AfterWrite = async (_, _) =>
    {
      if (moved)
        return;
      moved = true;
      await MoveReferenceAsync(f, off);
    };

    Assert.True(await f.Reconciler.ReconcileAsync(CameraId, default));
    Assert.False(f.CameraState.Armed);
    Assert.Equal(
      new List<(string CameraId, bool Armed)> { (CameraId, true), (CameraId, false) },
      f.Cfg.CameraFlags);
  }

  [Fact]
  public async Task AWriteOverwrittenByAConcurrentWriterIsCorrected()
  {
    var f = await CreateAsync();
    var armed = await SpaceAsync(f, FloorName, armed: true);
    await PlaceReferenceAsync(f, armed);

    // Another writer's value lands after ours, as the startup sweep and an event-driven reconcile
    // do to each other.
    var overwritten = false;
    f.Cfg.AfterWrite = (_, _) =>
    {
      if (!overwritten)
      {
        overwritten = true;
        f.CameraState.Armed = false;
      }

      return Task.CompletedTask;
    };

    Assert.True(await f.Reconciler.ReconcileAsync(CameraId, default));
    Assert.True(f.CameraState.Armed);
    Assert.Equal(2, f.Cfg.CameraFlags.Count);
  }

  [Fact]
  public async Task AReconcileThatCannotSettleStopsAtItsBudget()
  {
    var f = await CreateAsync();
    var armed = await SpaceAsync(f, FloorName, armed: true);
    await PlaceReferenceAsync(f, armed);

    // A value that keeps being replaced: the call has to give up instead of looping forever.
    f.Cfg.AfterWrite = (_, _) =>
    {
      f.CameraState.Armed = !f.CameraState.Armed;
      return Task.CompletedTask;
    };

    await f.Reconciler.ReconcileAsync(CameraId, default);

    Assert.Equal(CameraArmingReconciler.MaxWrites, f.Cfg.CameraFlags.Count);
  }

  [Fact]
  public async Task OneCameraThatFailsDoesNotStopTheOthers()
  {
    var f = await CreateAsync();
    var space = await SpaceAsync(f, FloorName, armed: true);
    await PlaceCameraAsync(f, CameraId, space);
    await PlaceCameraAsync(f, OtherCameraId, space);

    f.Cfg.BeforeWrite = (cameraId, _) => cameraId == CameraId
      ? Task.FromException(new InvalidOperationException("the write failed"))
      : Task.CompletedTask;

    await f.Reconciler.ReconcileManyAsync([CameraId, OtherCameraId], default);

    Assert.Contains((OtherCameraId, true), f.Cfg.CameraFlags);
  }
}
