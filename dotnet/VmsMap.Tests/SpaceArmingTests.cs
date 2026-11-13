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
using VmsMap.Outbox;

namespace VmsMap.Tests;

// Arming a container: the property on its node, the mode vocabulary, who may be armed at all, the
// journal entry and the outbox row that makes the re-check survive a crash.
public class SpaceArmingTests
{
  private const string CameraId = "3f2504e0-4f89-11d3-9a0c-0305e82c3301";
  private const string Token = "input1";

  private sealed class FakeSettings : IDeviceSettingsSource
  {
    public string? ArmMode { get; set; }

    public DeviceSettingsInfo? Row => ArmMode is null
      ? null
      : new DeviceSettingsInfo(
          CameraId, DeviceObjectId.DigitalInputPart, Token, "Door", ArmMode!, "closed", true);

    public Task<IReadOnlyList<DeviceSettingsInfo>> ListAsync(string cameraId, CancellationToken ct) =>
      Task.FromResult<IReadOnlyList<DeviceSettingsInfo>>(Row is null ? [] : [Row!]);

    public Task<DeviceSettingsInfo?> FindAsync(
      string cameraId, string kind, string token, CancellationToken ct) =>
      Task.FromResult(Row);

    public Task<IReadOnlyDictionary<string, IReadOnlyList<DeviceSettingsInfo>>> ListManyAsync(
      IReadOnlyCollection<string> cameraIds, CancellationToken ct) =>
      Task.FromResult<IReadOnlyDictionary<string, IReadOnlyList<DeviceSettingsInfo>>>(
        cameraIds.Distinct(StringComparer.Ordinal).ToDictionary(
          id => id,
          _ => (IReadOnlyList<DeviceSettingsInfo>)(Row is null ? [] : [Row!]),
          StringComparer.Ordinal));

    public void Invalidate(string cameraId) { }
  }

  private sealed record Fixture(
    VmsMapDbContext Db,
    MapDb Map,
    DeviceNodeService Nodes,
    NodeArmingService Arming,
    TestWiring.TestCfgArmingWriter Cfg,
    TestCameraState CameraState,
    FakeSettings Settings,
    string PartId);

  private static async Task<Fixture> CreateAsync(bool cameraArmed = false, string? armMode = null)
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
    var settings = new FakeSettings { ArmMode = armMode };
    var cameraState = new TestCameraState(cameraArmed);
    var cfg = new TestWiring.TestCfgArmingWriter(cameraState);
    var arming = TestWiring.NodeArming(db, map, cfg, cameraState, settings);

    var partId = await nodes.EnsureAsync(
      DeviceObjectId.CameraSource, CameraId, DeviceObjectId.DigitalInputPart, Token, default);

    return new Fixture(db, map, nodes, arming, cfg, cameraState, settings, partId!);
  }

  private static async Task<BaseMarkerDTO> NodeAsync(
    MapDb map, string name, string? parentId = null, string? ownerId = null)
  {
    var node = new BaseMarkerDTO
    {
      id = Guid.NewGuid().ToString(),
      name = name,
      parent_id = parentId,
      owner_id = ownerId,
    };
    await map.UpdateHierarchyAsync([node]);
    return node;
  }

  private static async Task<BaseMarkerDTO> SpaceAsync(
    Fixture f, string name, string? parentId = null)
  {
    var node = await NodeAsync(f.Map, name, parentId);
    f.Db.Spaces.Add(new DBSpace { object_id = Guid.Parse(node.id!), kind = SpaceKinds.Map });
    await f.Db.SaveChangesAsync();
    return node;
  }

  private static async Task<string> PlaceReferenceAsync(Fixture f, string spaceId)
  {
    var replica = await NodeAsync(
      f.Map, "Camera picture", parentId: spaceId, ownerId: CameraObjectId.For(CameraId));

    f.Db.CameraPlacements.Add(new DBCameraPlacement
    {
      object_id = Guid.Parse(replica.id!),
      camera_id = CameraId,
      is_reference = true,
    });
    await f.Db.SaveChangesAsync();
    return replica.id!;
  }

  private static async Task<string?> StoredPropertyAsync(Fixture f, string nodeId)
  {
    var id = Guid.Parse(nodeId);
    return (await f.Db.Properties.AsNoTracking()
      .FirstOrDefaultAsync(p => p.object_id == id
                                && p.prop_name == ArmMode.ArmedProperty))?.str_val;
  }

  [Fact]
  public async Task ArmingAContainerStoresTheBooleanTheResolverReads()
  {
    var f = await CreateAsync();
    var space = await SpaceAsync(f, "Floor");

    var view = await f.Arming.SetAsync(space.id!, ArmMode.Armed, "danil", default);

    // Not "armed": the resolver parses this value with bool.TryParse and would skip anything else.
    Assert.Equal("true", await StoredPropertyAsync(f, space.id!));
    Assert.True(view!.Arming!.Armed);
    Assert.Equal(EffectiveArmingResolver.Explicit, view.Arming.Source);
    Assert.Equal(ArmMode.Armed, view.Mode);
  }

  [Fact]
  public async Task InheritRemovesThePropertyRatherThanStoringAMode()
  {
    var f = await CreateAsync();
    var space = await SpaceAsync(f, "Floor");
    await f.Arming.SetAsync(space.id!, ArmMode.Armed, "danil", default);

    await f.Arming.SetAsync(space.id!, ArmMode.Inherit, "danil", default);

    Assert.Null(await StoredPropertyAsync(f, space.id!));
    var view = await f.Arming.GetAsync(space.id!, default);
    Assert.Equal(ArmMode.Inherit, view!.Mode);
    Assert.Equal(EffectiveArmingResolver.None, view.Arming!.Source);
  }

  [Fact]
  public async Task ASensorUnderAnArmedContainerResolvesToItAndNamesTheZone()
  {
    var f = await CreateAsync();
    var building = await NodeAsync(f.Map, "Building");
    var floor = await SpaceAsync(f, "Floor", building.id);
    await PlaceReferenceAsync(f, floor.id!);

    await f.Arming.SetAsync(building.id!, ArmMode.Armed, "danil", default);

    var view = await f.Arming.GetAsync(f.PartId, default);

    Assert.True(view!.Arming!.Armed);
    Assert.Equal(EffectiveArmingResolver.Space, view.Arming.Source);
    // The zone is the node that settled it, not the space the camera happens to stand in: that is
    // what the alarm list groups by.
    Assert.Equal(building.id, view.Arming.SourceObjectId);
  }

  [Fact]
  public async Task ADisarmedFloorInsideAnArmedBuildingIsDisarmed()
  {
    var f = await CreateAsync();
    var building = await NodeAsync(f.Map, "Building");
    var floor = await SpaceAsync(f, "Floor", building.id);
    await PlaceReferenceAsync(f, floor.id!);

    await f.Arming.SetAsync(building.id!, ArmMode.Armed, "danil", default);
    await f.Arming.SetAsync(floor.id!, ArmMode.Disarmed, "danil", default);

    var view = await f.Arming.GetAsync(f.PartId, default);

    Assert.False(view!.Arming!.Armed);
    Assert.Equal(floor.id, view.Arming.SourceObjectId);
  }

  [Fact]
  public async Task ASpaceIsArmableBeforeAnyCameraStandsInIt()
  {
    var f = await CreateAsync();
    var space = await SpaceAsync(f, "Floor");

    var view = await f.Arming.GetAsync(space.id!, default);

    Assert.True(view!.Armable);
    Assert.True(view.Configured);
    Assert.Equal([ArmMode.Inherit, ArmMode.Armed, ArmMode.Disarmed], view.Modes);
  }

  [Fact]
  public async Task AContainerNoCameraStandsThroughIsNotArmable()
  {
    var f = await CreateAsync();
    var drawing = await NodeAsync(f.Map, "A picture of the camera's parts");

    var view = await f.Arming.GetAsync(drawing.id!, default);

    Assert.False(view!.Armable);

    await Assert.ThrowsAsync<ArmingException>(() =>
      f.Arming.SetAsync(drawing.id!, ArmMode.Armed, "danil", default));
  }

  [Fact]
  public async Task AContainerWithAReferenceReplicaInItsSubtreeIsArmable()
  {
    var f = await CreateAsync();
    var zone = await NodeAsync(f.Map, "Warehouse");
    var floor = await SpaceAsync(f, "Floor", zone.id);
    await PlaceReferenceAsync(f, floor.id!);

    var view = await f.Arming.GetAsync(zone.id!, default);

    Assert.True(view!.Armable);
    Assert.True(view.Configured);
  }

  [Fact]
  public async Task ACameraPictureResolvesThroughItsOwner()
  {
    var f = await CreateAsync(cameraArmed: true);
    var space = await SpaceAsync(f, "Floor");
    var picture = await PlaceReferenceAsync(f, space.id!);

    var view = await f.Arming.GetAsync(picture, default);

    Assert.True(view!.Found);
    Assert.True(view.Armable);
    Assert.True(view.Arming!.Armed);
    Assert.Equal(EffectiveArmingResolver.Camera, view.Arming.Source);
  }

  [Fact]
  public async Task ACameraIsDelegatedToVmsCfgAndJournalled()
  {
    var f = await CreateAsync();
    var cameraNode = CameraObjectId.For(CameraId);

    var view = await f.Arming.SetAsync(cameraNode, ArmMode.Armed, "danil", default);

    Assert.Equal((CameraId, true), Assert.Single(f.Cfg.CameraFlags));
    Assert.True(view!.Arming!.Armed);
    Assert.Equal(ArmMode.Armed, view.Mode);

    var entry = Assert.Single(await f.Db.Events.ToListAsync());
    Assert.Equal(NodeArmingService.JournalEventName, entry.event_name);
    Assert.Equal("danil", entry.param0);
    Assert.Equal(ArmMode.Armed, entry.param1);
  }

  [Fact]
  public async Task AnInputIsDelegatedWithItsTokenAndNotJournalledTwice()
  {
    var f = await CreateAsync(armMode: ArmMode.Inherit);

    await f.Arming.SetAsync(f.PartId, ArmMode.Armed, "danil", default);

    Assert.Equal((CameraId, Token, ArmMode.Armed), Assert.Single(f.Cfg.PartModes));
    Assert.Single(await f.Db.Events.ToListAsync());
  }

  [Fact]
  public async Task ARelayIsNotArmable()
  {
    var f = await CreateAsync();
    var relayId = (await f.Nodes.EnsureAsync(
      DeviceObjectId.CameraSource, CameraId, DeviceObjectId.RelayPart, "lamp", default))!;

    var view = await f.Arming.GetAsync(relayId, default);

    Assert.False(view!.Armable);
    Assert.Null(view.Arming);
    await Assert.ThrowsAsync<ArmingException>(() =>
      f.Arming.SetAsync(relayId, ArmMode.Armed, "danil", default));
  }

  [Fact]
  public async Task AnUnconfiguredInputIsArmableInKindButHasNowhereToKeepTheMode()
  {
    var f = await CreateAsync();

    var view = await f.Arming.GetAsync(f.PartId, default);

    Assert.True(view!.Armable);
    Assert.False(view.Configured);

    var refused = await Assert.ThrowsAsync<ArmingException>(() =>
      f.Arming.SetAsync(f.PartId, ArmMode.Armed, "danil", default));
    Assert.Equal(ArmingRefusal.NotConfigured, refused.Refusal);
    Assert.Empty(f.Cfg.PartModes);
  }

  [Fact]
  public async Task AnUnknownModeIsRefusedBeforeAnythingIsWritten()
  {
    var f = await CreateAsync();
    var space = await SpaceAsync(f, "Floor");

    var refused = await Assert.ThrowsAsync<ArmingException>(() =>
      f.Arming.SetAsync(space.id!, "always", "danil", default));

    Assert.Equal(ArmingRefusal.InvalidRequest, refused.Refusal);
    Assert.Null(await StoredPropertyAsync(f, space.id!));
  }

  [Fact]
  public async Task AChangeQueuesTheRecheckInTheSameTransaction()
  {
    var f = await CreateAsync();
    var space = await SpaceAsync(f, "Floor");

    await f.Arming.SetAsync(space.id!, ArmMode.Armed, "danil", default);

    var row = Assert.Single(await f.Db.MapOutbox.ToListAsync());
    Assert.Equal(MapTopics.ArmingChanged, row.topic);
    Assert.Contains(space.id!, row.payload, StringComparison.Ordinal);
  }

  [Fact]
  public async Task RepeatingTheSameModeJournalsButDoesNotQueueARecheck()
  {
    var f = await CreateAsync();
    var space = await SpaceAsync(f, "Floor");

    await f.Arming.SetAsync(space.id!, ArmMode.Armed, "danil", default);
    await f.Arming.SetAsync(space.id!, ArmMode.Armed, "danil", default);

    Assert.Single(await f.Db.MapOutbox.ToListAsync());
    // Both commands are the operator's actions, so both stay in the journal.
    Assert.Equal(2, (await f.Db.Events.ToListAsync()).Count);
  }

  [Fact]
  public async Task ADisarmedContainerStillJournals()
  {
    var f = await CreateAsync();
    var space = await SpaceAsync(f, "Floor");

    await f.Arming.SetAsync(space.id!, ArmMode.Disarmed, "danil", default);

    Assert.Equal("false", await StoredPropertyAsync(f, space.id!));
    Assert.Equal(
      ArmMode.Disarmed, Assert.Single(await f.Db.Events.ToListAsync()).param1);
  }

  [Fact]
  public async Task AnUnknownNodeIsNotFound()
  {
    var f = await CreateAsync();

    Assert.Null(await f.Arming.GetAsync(Guid.NewGuid().ToString(), default));
    var refused = await Assert.ThrowsAsync<ArmingException>(() =>
      f.Arming.SetAsync(Guid.NewGuid().ToString(), ArmMode.Armed, "danil", default));
    Assert.Equal(ArmingRefusal.NotFound, refused.Refusal);
  }

  [Fact]
  public async Task AnOperatorNameIsNotRequiredForTheJournal()
  {
    var f = await CreateAsync();
    var space = await SpaceAsync(f, "Floor");

    await f.Arming.SetAsync(space.id!, ArmMode.Armed, "  ", default);

    Assert.Equal(NodeArmingService.SystemActor,
      Assert.Single(await f.Db.Events.ToListAsync()).param0);
  }
}
