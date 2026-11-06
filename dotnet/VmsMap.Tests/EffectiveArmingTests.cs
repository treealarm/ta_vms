using System;
using System.Collections.Generic;
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

  private sealed record Fixture(
    VmsMapDbContext Db,
    MapDb Map,
    DeviceNodeService Nodes,
    TestCameraState CameraState,
    EffectiveArmingResolver Resolver,
    FakeSettings Settings,
    string PartId);

  private sealed class FakeSettings : IDeviceSettingsSource
  {
    public string? ArmMode { get; set; }

    private DeviceSettingsInfo? Row => ArmMode is null
      ? null
      : new DeviceSettingsInfo(
          CameraId, DeviceObjectId.DigitalInputPart, Token, "", ArmMode!, null, false);

    public Task<IReadOnlyList<DeviceSettingsInfo>> ListAsync(string cameraId, CancellationToken ct) =>
      Task.FromResult<IReadOnlyList<DeviceSettingsInfo>>(Row is null ? [] : [Row]);

    public Task<DeviceSettingsInfo?> FindAsync(
      string cameraId, string kind, string token, CancellationToken ct) =>
      Task.FromResult(Row);

    public void Invalidate(string cameraId) { }
  }

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
    var updates = new MapUpdateService(map, tracks, states);

    var nodes = TestWiring.DeviceNodes(db, map, updates);
    var settings = new FakeSettings();
    var cameraState = new TestCameraState(cameraArmed);
    var resolver = new EffectiveArmingResolver(db, settings, cameraState);

    var partId = await nodes.EnsureAsync(
      DeviceObjectId.CameraSource, CameraId, DeviceObjectId.DigitalInputPart, Token, default);

    return new Fixture(db, map, nodes, cameraState, resolver, settings, partId!);
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
    var replica = await Node(f.Map, "Camera on the map", parentId: spaceId, ownerId: CameraObjectId.For(CameraId));

    f.Db.CameraPlacements.Add(new DBCameraPlacement
    {
      object_id = Guid.Parse(replica.id!),
      camera_id = CameraId,
      is_reference = true,
    });

    if (propertyValue is { } value)
    {
      f.Db.Properties.Add(new DBMarkerProp
      {
        id = Guid.NewGuid(),
        object_id = Guid.Parse(propertyOwnerId),
        prop_name = EffectiveArmingResolver.ArmedProperty,
        str_val = value ? "true" : "false",
      });
    }

    await f.Db.SaveChangesAsync();
  }

  [Fact]
  public async Task AnInputsOwnModeWinsOverEverythingElse()
  {
    var f = await CreateAsync(cameraArmed: false);
    f.Settings.ArmMode = "armed";

    var arming = await f.Resolver.ResolveAsync(f.PartId, default);

    Assert.NotNull(arming);
    Assert.True(arming!.Armed);
    Assert.Equal(EffectiveArmingResolver.Explicit, arming.Source);
  }

  [Fact]
  public async Task ADisarmedInputStaysDisarmedEvenWhenItsSpaceIsArmed()
  {
    var f = await CreateAsync(cameraArmed: true);
    var space = await Node(f.Map, "Floor");
    await PlaceReferenceAsync(f, space.id!, space.id!, true);
    f.Settings.ArmMode = "disarmed";

    var arming = await f.Resolver.ResolveAsync(f.PartId, default);

    Assert.False(arming!.Armed);
    Assert.Equal(EffectiveArmingResolver.Explicit, arming.Source);
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
    var space = await Node(f.Map, "Floor");
    await PlaceReferenceAsync(f, space.id!, space.id!, true);

    var arming = await f.Resolver.ResolveAsync(f.PartId, default);

    Assert.True(arming!.Armed);
    Assert.Equal(EffectiveArmingResolver.Space, arming.Source);
  }

  [Fact]
  public async Task TheNearestExplicitPropertyWins()
  {
    var f = await CreateAsync(cameraArmed: true);
    var building = await Node(f.Map, "Building");
    var floor = await Node(f.Map, "Floor", parentId: building.id);

    // The building is armed; this floor is explicitly disarmed.
    await PlaceReferenceAsync(f, floor.id!, building.id!, true);
    f.Db.Properties.Add(new DBMarkerProp
    {
      id = Guid.NewGuid(),
      object_id = Guid.Parse(floor.id!),
      prop_name = EffectiveArmingResolver.ArmedProperty,
      str_val = "false",
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
    var space = await Node(f.Map, "Floor");
    await PlaceReferenceAsync(f, space.id!, space.id!, true);

    var arming = await f.Resolver.ResolveAsync(CameraObjectId.For(CameraId), default);

    Assert.True(arming!.Armed);
    Assert.Equal(EffectiveArmingResolver.Space, arming.Source);
  }

  [Fact]
  public async Task AnIdThatIsNotADeviceNodeHasNoArming()
  {
    var f = await CreateAsync();
    var space = await Node(f.Map, "Floor");

    Assert.Null(await f.Resolver.ResolveAsync(space.id!, default));
  }
}
