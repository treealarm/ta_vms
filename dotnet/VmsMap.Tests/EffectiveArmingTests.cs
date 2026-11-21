using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using VmsMap.Adapter;
using VmsMap.Clients;
using VmsMap.Db;
using VmsMap.Domain;
using VmsMap.Domain.Arming;

namespace VmsMap.Tests;

// Arming has three sources: the part's own mode, an armed property of the space the camera stands
// in, and the camera's own flag. The canonical node hangs under the server, so the "space" is not
// its ancestor — it is the canvas of its reference replica.
public class EffectiveArmingTests
{
  private const string CameraId = "3f2504e0-4f89-11d3-9a0c-0305e82c3301";
  private const string Token = "input1";

  // The fixtures these tests share.
  private const string FloorName = "floor";

  private sealed record Fixture(
    VmsMapDbContext Db,
    MapDb Map,
    DeviceNodeService Nodes,
    TestCameraState CameraState,
    EffectiveArmingResolver Resolver,
    DeviceObjectSettingsStore Store,
    string PartId);

  private static async Task<Fixture> CreateAsync(bool cameraArmed = false)
  {
    var options = new DbContextOptionsBuilder<VmsMapDbContext>()
      .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
      .Options;

    var db = new VmsMapDbContext(options);
    var map = new MapDb(db);
    var stateService = new StateService(db);
    var states = new StatesUpdateService(stateService, map, db);
    var levels = new LevelService(db);
    var tracks = new TracksUpdateService(map, new TrackService(db, levels), new GeoService(db, levels));
    var updates = new MapUpdateService(db, map, tracks, states);

    var nodes = TestWiring.DeviceNodes(db, map, updates);
    var store = TestWiring.ObjectSettings(db);
    var cameraState = new TestCameraState(cameraArmed);
    var resolver = new EffectiveArmingResolver(db, store, cameraState);

    var partId = await nodes.EnsureAsync(
      DeviceObjectId.CameraSource, CameraId, DeviceObjectId.DigitalInputPart, Token, default);

    return new Fixture(db, map, nodes, cameraState, resolver, store, partId!);
  }

  private static async Task<BaseMarkerDTO> Node(
    MapDb map, string name, string? parentId = null, string? ownerId = null)
  {
    var node = new BaseMarkerDTO { id = Guid.NewGuid().ToString(), name = name, parent_id = parentId, owner_id = ownerId };
    await map.UpdateHierarchyAsync([node]);
    return node;
  }

  private static async Task PlaceReferenceAsync(
    Fixture f, string spaceId, string propertyOwnerId, bool? propertyValue)
  {
    var replica = await Node(f.Map, "camera on the map", parentId: spaceId, ownerId: CameraObjectId.For(CameraId));

    f.Db.CameraPlacements.Add(new DBCameraPlacement
    {
      object_id = Guid.Parse(replica.id!),
      camera_id = CameraId,
      is_reference = true,
    });

    if (propertyValue is { } value)
    {
      f.Db.ObjectArming.Add(new DBObjectArming
      {
        object_id = Guid.Parse(propertyOwnerId),
        armed = value,
      });
    }

    await f.Db.SaveChangesAsync();
  }

  [Fact]
  public async Task AnInputsOwnModeWinsOverEverythingElse()
  {
    var f = await CreateAsync(cameraArmed: false);
    await f.Store.SetAsync(f.PartId, ArmMode.Armed, null, false, default);

    var arming = await f.Resolver.ResolveAsync(f.PartId, default);

    Assert.NotNull(arming);
    Assert.True(arming!.Armed);
    Assert.Equal(EffectiveArmingResolver.Explicit, arming.Source);
  }

  [Fact]
  public async Task ADisarmedInputStaysDisarmedEvenWhenItsSpaceIsArmed()
  {
    var f = await CreateAsync(cameraArmed: true);
    var space = await Node(f.Map, FloorName);
    await PlaceReferenceAsync(f, space.id!, space.id!, true);
    await f.Store.SetAsync(f.PartId, ArmMode.Disarmed, null, false, default);

    var arming = await f.Resolver.ResolveAsync(f.PartId, default);

    // The answer does not change: the input's own row still wins. What changes is the reading —
    // the zone above is armed, so the input is excluded rather than simply off.
    Assert.False(arming!.Armed);
    Assert.Equal(EffectiveArmingResolver.Explicit, arming.Source);
    Assert.True(arming.Bypassed);
    Assert.Equal(space.id, arming.SourceObjectId);
  }

  [Fact]
  public async Task ADisarmedInputUnderAnOffZoneIsNotBypassed()
  {
    var f = await CreateAsync(cameraArmed: true);
    var space = await Node(f.Map, FloorName);
    await PlaceReferenceAsync(f, space.id!, space.id!, false);
    await f.Store.SetAsync(f.PartId, ArmMode.Disarmed, null, false, default);

    var arming = await f.Resolver.ResolveAsync(f.PartId, default);

    Assert.False(arming!.Armed);
    Assert.False(arming.Bypassed);
    Assert.Equal(string.Empty, arming.SourceObjectId);
  }

  [Fact]
  public async Task TheNearestExplicitRowAboveTheZoneSettlesTheBypass()
  {
    var f = await CreateAsync(cameraArmed: true);
    var building = await Node(f.Map, "building");
    var floor = await Node(f.Map, FloorName, parentId: building.id);

    // The floor carries no row; the building is armed, so the floor inherits guard. An input
    // disarmed on that floor is excluded from the building.
    await PlaceReferenceAsync(f, floor.id!, building.id!, true);
    await f.Store.SetAsync(f.PartId, ArmMode.Disarmed, null, false, default);

    var arming = await f.Resolver.ResolveAsync(f.PartId, default);

    Assert.False(arming!.Armed);
    Assert.True(arming.Bypassed);
    Assert.Equal(building.id, arming.SourceObjectId);
  }

  [Fact]
  public async Task ADisarmedInputWithNoReferenceReplicaIsNotBypassed()
  {
    var f = await CreateAsync(cameraArmed: true);
    await f.Store.SetAsync(f.PartId, ArmMode.Disarmed, null, false, default);

    var arming = await f.Resolver.ResolveAsync(f.PartId, default);

    Assert.False(arming!.Armed);
    Assert.False(arming.Bypassed);
    Assert.Equal(string.Empty, arming.SourceObjectId);
  }

  [Fact]
  public async Task ADisarmedCameraInAnArmedZoneIsBypassed()
  {
    var f = await CreateAsync(cameraArmed: false);
    var space = await Node(f.Map, FloorName);
    await PlaceReferenceAsync(f, space.id!, space.id!, true);
    await f.Store.SetAsync(CameraObjectId.For(CameraId), ArmMode.Disarmed, null, false, default);

    var arming = await f.Resolver.ResolveAsync(CameraObjectId.For(CameraId), default);

    Assert.False(arming!.Armed);
    Assert.True(arming.Bypassed);
    Assert.Equal(space.id, arming.SourceObjectId);
  }

  [Fact]
  public async Task ADisarmedSpaceUnderAnArmedAncestorIsBypassed()
  {
    var f = await CreateAsync();
    var building = await Node(f.Map, "building");
    var floor = await Node(f.Map, FloorName, parentId: building.id);
    f.Db.ObjectArming.Add(new DBObjectArming { object_id = Guid.Parse(building.id!), armed = true });
    f.Db.ObjectArming.Add(new DBObjectArming { object_id = Guid.Parse(floor.id!), armed = false });
    await f.Db.SaveChangesAsync();

    var arming = await f.Resolver.ResolveSpaceAsync(floor.id!, default);

    Assert.False(arming!.Armed);
    Assert.True(arming.Bypassed);
    Assert.Equal(building.id, arming.SourceObjectId);
  }

  [Fact]
  public async Task ADisarmedSpaceUnderADisarmedAncestorIsNotBypassed()
  {
    var f = await CreateAsync();
    var building = await Node(f.Map, "building");
    var floor = await Node(f.Map, FloorName, parentId: building.id);
    f.Db.ObjectArming.Add(new DBObjectArming { object_id = Guid.Parse(building.id!), armed = false });
    f.Db.ObjectArming.Add(new DBObjectArming { object_id = Guid.Parse(floor.id!), armed = false });
    await f.Db.SaveChangesAsync();

    var arming = await f.Resolver.ResolveSpaceAsync(floor.id!, default);

    Assert.False(arming!.Armed);
    Assert.False(arming.Bypassed);
    Assert.Equal(floor.id, arming.SourceObjectId);
  }

  [Fact]
  public async Task AnInheritingSpaceIsNotBypassed()
  {
    var f = await CreateAsync();
    var building = await Node(f.Map, "building");
    var floor = await Node(f.Map, FloorName, parentId: building.id);
    f.Db.ObjectArming.Add(new DBObjectArming { object_id = Guid.Parse(building.id!), armed = true });
    await f.Db.SaveChangesAsync();

    var arming = await f.Resolver.ResolveSpaceAsync(floor.id!, default);

    Assert.True(arming!.Armed);
    Assert.Equal(EffectiveArmingResolver.Inherited, arming.Source);
    Assert.False(arming.Bypassed);
  }

  [Fact]
  public async Task MovingTheReferenceReplicaRecomputesTheBypass()
  {
    var f = await CreateAsync(cameraArmed: false);
    var armed = await Node(f.Map, "armed floor");
    var off = await Node(f.Map, "off floor");
    await PlaceReferenceAsync(f, armed.id!, armed.id!, true);
    await f.Store.SetAsync(f.PartId, ArmMode.Disarmed, null, false, default);

    var before = await f.Resolver.ResolveAsync(f.PartId, default);
    Assert.True(before!.Bypassed);

    var placement = await f.Db.CameraPlacements.SingleAsync(
      p => p.camera_id == CameraId && p.is_reference);
    // `object_id` is the placement's key: the reference moves by replacing the row, not by
    // editing the key in place.
    f.Db.CameraPlacements.Remove(placement);
    await f.Db.SaveChangesAsync();

    var moved = await Node(
      f.Map, "camera elsewhere", parentId: off.id, ownerId: CameraObjectId.For(CameraId));
    f.Db.CameraPlacements.Add(new DBCameraPlacement
    {
      object_id = Guid.Parse(moved.id!),
      camera_id = CameraId,
      is_reference = true,
    });
    await f.Db.SaveChangesAsync();

    var after = await f.Resolver.ResolveAsync(f.PartId, default);

    Assert.False(after!.Bypassed);
    Assert.Equal(string.Empty, after.SourceObjectId);
  }

  [Fact]
  public async Task WithoutAReferenceReplicaTheCameraIsTheFallback()
  {
    var f = await CreateAsync(cameraArmed: true);

    var arming = await f.Resolver.ResolveAsync(f.PartId, default);

    Assert.True(arming!.Armed);
    Assert.Equal(EffectiveArmingResolver.Camera, arming.Source);
  }

  [Fact]
  public async Task AnArmedPropertyOfTheReferenceReplicasSpaceIsInherited()
  {
    var f = await CreateAsync(cameraArmed: false);
    var space = await Node(f.Map, FloorName);
    await PlaceReferenceAsync(f, space.id!, space.id!, true);

    var arming = await f.Resolver.ResolveAsync(f.PartId, default);

    Assert.True(arming!.Armed);
    Assert.Equal(EffectiveArmingResolver.Space, arming.Source);
  }

  [Fact]
  public async Task TheNearestExplicitPropertyWins()
  {
    var f = await CreateAsync(cameraArmed: true);
    var building = await Node(f.Map, "building");
    var floor = await Node(f.Map, FloorName, parentId: building.id);

    // The building is armed; this floor is explicitly disarmed.
    await PlaceReferenceAsync(f, floor.id!, building.id!, true);
    f.Db.ObjectArming.Add(new DBObjectArming
    {
      object_id = Guid.Parse(floor.id!),
      armed = false,
    });
    await f.Db.SaveChangesAsync();

    var arming = await f.Resolver.ResolveAsync(f.PartId, default);

    Assert.False(arming!.Armed);
    Assert.Equal(EffectiveArmingResolver.Space, arming.Source);
  }

  [Fact]
  public async Task ACameraNodeInheritsLikeItsParts()
  {
    var f = await CreateAsync(cameraArmed: false);
    var space = await Node(f.Map, FloorName);
    await PlaceReferenceAsync(f, space.id!, space.id!, true);

    var arming = await f.Resolver.ResolveAsync(CameraObjectId.For(CameraId), default);

    Assert.True(arming!.Armed);
    Assert.Equal(EffectiveArmingResolver.Space, arming.Source);
  }

  [Fact]
  public async Task ACamerasOwnModeComesFromTheMapAndWinsOverItsZone()
  {
    var f = await CreateAsync(cameraArmed: false);
    var space = await Node(f.Map, FloorName);
    await PlaceReferenceAsync(f, space.id!, space.id!, true);
    await f.Store.SetAsync(
      CameraObjectId.For(CameraId), ArmMode.Disarmed, null, false, default);

    var arming = await f.Resolver.ResolveAsync(CameraObjectId.For(CameraId), default);

    Assert.False(arming!.Armed);
    Assert.Equal(EffectiveArmingResolver.Explicit, arming.Source);
    Assert.Equal(ArmMode.Disarmed, arming.Mode);
  }

  [Fact]
  public async Task AnIdThatIsNotADeviceNodeHasNoArming()
  {
    var f = await CreateAsync();
    var space = await Node(f.Map, FloorName);

    Assert.Null(await f.Resolver.ResolveAsync(space.id!, default));
  }
}
