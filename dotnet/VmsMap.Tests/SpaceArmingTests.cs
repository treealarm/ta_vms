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

  // The fixtures these tests share. The space names are labels; the operator is the journal's actor.
  private const string BuildingName = "building";
  private const string FloorName = "floor";
  private const string DanilName = "danil";

  private sealed class FakeSettings : IDeviceSettingsSource
  {
    /// <summary>Whether the administrator gave the input a settings row at all.</summary>
    public bool Configured { get; set; }

    public DeviceSettingsInfo? Row => Configured
      ? new DeviceSettingsInfo(
          CameraId, DeviceObjectId.DigitalInputPart, Token, "door", ArmMode.Inherit, "closed", true)
      : null;

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
    DeviceObjectSettingsStore ObjectSettings,
    string PartId);

  private static async Task<Fixture> CreateAsync(
    bool cameraArmed = false, string? armMode = null, string cameraMode = ArmMode.Inherit)
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
    var settings = new FakeSettings { Configured = armMode is not null };
    var cameraState = new TestCameraState(cameraArmed, cameraMode);
    var cfg = new TestWiring.TestCfgArmingWriter(cameraState);
    var arming = TestWiring.NodeArming(db, map, cfg, cameraState, settings);
    var objectSettings = TestWiring.ObjectSettings(db);

    var partId = await nodes.EnsureAsync(
      DeviceObjectId.CameraSource, CameraId, DeviceObjectId.DigitalInputPart, Token, default);
    if (armMode is not null)
      await objectSettings.SetModeAsync(partId!, armMode, default);
    if (!string.Equals(cameraMode, ArmMode.Inherit, StringComparison.Ordinal))
      await objectSettings.SetModeAsync(CameraObjectId.For(CameraId), cameraMode, default);

    return new Fixture(db, map, nodes, arming, cfg, cameraState, settings, objectSettings, partId!);
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
      f.Map, "camera picture", parentId: spaceId, ownerId: CameraObjectId.For(CameraId));

    f.Db.CameraPlacements.Add(new DBCameraPlacement
    {
      object_id = Guid.Parse(replica.id!),
      camera_id = CameraId,
      is_reference = true,
    });
    await f.Db.SaveChangesAsync();
    return replica.id!;
  }

  private static async Task<bool?> StoredArmingAsync(Fixture f, string nodeId)
  {
    var id = Guid.Parse(nodeId);
    return (await f.Db.ObjectArming.AsNoTracking()
      .FirstOrDefaultAsync(a => a.object_id == id))?.armed;
  }

  [Fact]
  public async Task ArmingAContainerStoresTheBooleanTheResolverReads()
  {
    var f = await CreateAsync();
    var space = await SpaceAsync(f, FloorName);

    var view = await f.Arming.SetAsync(space.id!, ArmMode.Armed, DanilName, default);

    // Not "armed": the resolver parses this value with bool.TryParse and would skip anything else.
    Assert.True(await StoredArmingAsync(f, space.id!));
    Assert.True(view!.Arming!.Armed);
    Assert.Equal(EffectiveArmingResolver.Explicit, view.Arming.Source);
    Assert.Equal(ArmMode.Armed, view.Mode);
  }

  [Fact]
  public async Task InheritRemovesThePropertyRatherThanStoringAMode()
  {
    var f = await CreateAsync();
    var space = await SpaceAsync(f, FloorName);
    await f.Arming.SetAsync(space.id!, ArmMode.Armed, DanilName, default);

    await f.Arming.SetAsync(space.id!, ArmMode.Inherit, DanilName, default);

    Assert.Null(await StoredArmingAsync(f, space.id!));
    var view = await f.Arming.GetAsync(space.id!, default);
    Assert.Equal(ArmMode.Inherit, view!.Mode);
    Assert.Equal(EffectiveArmingResolver.None, view.Arming!.Source);
  }

  [Fact]
  public async Task ASensorUnderAnArmedContainerResolvesToItAndNamesTheZone()
  {
    var f = await CreateAsync();
    var building = await NodeAsync(f.Map, BuildingName);
    var floor = await SpaceAsync(f, FloorName, building.id);
    await PlaceReferenceAsync(f, floor.id!);

    await f.Arming.SetAsync(building.id!, ArmMode.Armed, DanilName, default);

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
    var building = await NodeAsync(f.Map, BuildingName);
    var floor = await SpaceAsync(f, FloorName, building.id);
    await PlaceReferenceAsync(f, floor.id!);

    await f.Arming.SetAsync(building.id!, ArmMode.Armed, DanilName, default);
    await f.Arming.SetAsync(floor.id!, ArmMode.Disarmed, DanilName, default);

    var view = await f.Arming.GetAsync(f.PartId, default);

    Assert.False(view!.Arming!.Armed);
    Assert.Equal(floor.id, view.Arming.SourceObjectId);
  }

  [Fact]
  public async Task ASpaceIsArmableBeforeAnyCameraStandsInIt()
  {
    var f = await CreateAsync();
    var space = await SpaceAsync(f, FloorName);

    var view = await f.Arming.GetAsync(space.id!, default);

    Assert.True(view!.Armable);
    Assert.True(view.Configured);
    Assert.Equal([ArmMode.Inherit, ArmMode.Armed, ArmMode.Disarmed], view.Modes);
  }

  [Fact]
  public async Task AContainerNoCameraStandsThroughIsNotArmable()
  {
    var f = await CreateAsync();
    var drawing = await NodeAsync(f.Map, "a picture of the camera's parts");

    var view = await f.Arming.GetAsync(drawing.id!, default);

    Assert.False(view!.Armable);

    await Assert.ThrowsAsync<ArmingException>(() =>
      f.Arming.SetAsync(drawing.id!, ArmMode.Armed, DanilName, default));
  }

  [Fact]
  public async Task AContainerWithAReferenceReplicaInItsSubtreeIsArmable()
  {
    var f = await CreateAsync();
    var zone = await NodeAsync(f.Map, "warehouse");
    var floor = await SpaceAsync(f, FloorName, zone.id);
    await PlaceReferenceAsync(f, floor.id!);

    var view = await f.Arming.GetAsync(zone.id!, default);

    Assert.True(view!.Armable);
    Assert.True(view.Configured);
  }

  [Fact]
  public async Task ACameraPictureResolvesThroughItsOwner()
  {
    var f = await CreateAsync(cameraArmed: true, cameraMode: ArmMode.Armed);
    var space = await SpaceAsync(f, FloorName);
    var picture = await PlaceReferenceAsync(f, space.id!);

    var view = await f.Arming.GetAsync(picture, default);

    Assert.True(view!.Found);
    Assert.True(view.Armable);
    Assert.True(view.Arming!.Armed);
    Assert.Equal(EffectiveArmingResolver.Explicit, view.Arming.Source);
    Assert.Equal(ArmMode.Armed, view.Mode);
  }

  [Fact]
  public async Task ACameraOnInheritWithNoZoneIsOff()
  {
    var f = await CreateAsync(cameraArmed: true);
    var space = await SpaceAsync(f, FloorName);
    var picture = await PlaceReferenceAsync(f, space.id!);

    var view = await f.Arming.GetAsync(picture, default);

    Assert.False(view!.Arming!.Armed);
    Assert.Equal(EffectiveArmingResolver.None, view.Arming.Source);
    Assert.Equal(ArmMode.Inherit, view.Mode);
  }

  [Fact]
  public async Task ACameraIsDelegatedToVmsCfgAndJournalled()
  {
    var f = await CreateAsync();
    var cameraNode = CameraObjectId.For(CameraId);

    var view = await f.Arming.SetAsync(cameraNode, ArmMode.Armed, DanilName, default);

    Assert.Equal((CameraId, true), Assert.Single(f.Cfg.CameraFlags));
    Assert.True(view!.Arming!.Armed);
    Assert.Equal(ArmMode.Armed, view.Mode);

    var entry = Assert.Single(await f.Db.Events.ToListAsync());
    Assert.Equal(NodeArmingService.JournalEventName, entry.event_name);
    Assert.Equal(DanilName, entry.param0);
    Assert.Equal(ArmMode.Armed, entry.param1);
  }

  [Fact]
  public async Task AnInputIsDelegatedWithItsTokenAndNotJournalledTwice()
  {
    var f = await CreateAsync(armMode: ArmMode.Inherit);

    await f.Arming.SetAsync(f.PartId, ArmMode.Armed, DanilName, default);

    Assert.Equal(ArmMode.Armed, (await f.ObjectSettings.GetAsync(f.PartId, default)).Mode);
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
      f.Arming.SetAsync(relayId, ArmMode.Armed, DanilName, default));
  }

  [Fact]
  public async Task AnUnconfiguredInputIsArmableInKindButHasNowhereToKeepTheMode()
  {
    var f = await CreateAsync();

    var view = await f.Arming.GetAsync(f.PartId, default);

    Assert.True(view!.Armable);
    Assert.False(view.Configured);

    var refused = await Assert.ThrowsAsync<ArmingException>(() =>
      f.Arming.SetAsync(f.PartId, ArmMode.Armed, DanilName, default));
    Assert.Equal(ArmingRefusal.NotConfigured, refused.Refusal);
    Assert.Equal(ArmMode.Inherit, (await f.ObjectSettings.GetAsync(f.PartId, default)).Mode);
  }

  [Fact]
  public async Task AnUnknownModeIsRefusedBeforeAnythingIsWritten()
  {
    var f = await CreateAsync();
    var space = await SpaceAsync(f, FloorName);

    var refused = await Assert.ThrowsAsync<ArmingException>(() =>
      f.Arming.SetAsync(space.id!, "always", DanilName, default));

    Assert.Equal(ArmingRefusal.InvalidRequest, refused.Refusal);
    Assert.Null(await StoredArmingAsync(f, space.id!));
  }

  [Fact]
  public async Task AChangeQueuesTheRecheckInTheSameTransaction()
  {
    var f = await CreateAsync();
    var space = await SpaceAsync(f, FloorName);

    await f.Arming.SetAsync(space.id!, ArmMode.Armed, DanilName, default);

    var row = Assert.Single(await f.Db.MapOutbox.ToListAsync());
    Assert.Equal(MapTopics.ArmingChanged, row.topic);
    Assert.Contains(space.id!, row.payload, StringComparison.Ordinal);
  }

  [Fact]
  public async Task RepeatingTheSameModeJournalsButDoesNotQueueARecheck()
  {
    var f = await CreateAsync();
    var space = await SpaceAsync(f, FloorName);

    await f.Arming.SetAsync(space.id!, ArmMode.Armed, DanilName, default);
    await f.Arming.SetAsync(space.id!, ArmMode.Armed, DanilName, default);

    Assert.Single(await f.Db.MapOutbox.ToListAsync());
    // Both commands are the operator's actions, so both stay in the journal.
    const int operatorCommands = 2;
    Assert.Equal(operatorCommands, (await f.Db.Events.ToListAsync()).Count);
  }

  [Fact]
  public async Task ADisarmedContainerStillJournals()
  {
    var f = await CreateAsync();
    var space = await SpaceAsync(f, FloorName);

    await f.Arming.SetAsync(space.id!, ArmMode.Disarmed, DanilName, default);

    Assert.False(await StoredArmingAsync(f, space.id!));
    Assert.Equal(
      ArmMode.Disarmed, Assert.Single(await f.Db.Events.ToListAsync()).param1);
  }

  // ── A timed exclusion ───────────────────────────────────────────────────────────────────────

  [Fact]
  public async Task ATimedDisarmOfAnArmedContainerRestoresArmed()
  {
    var f = await CreateAsync();
    var space = await SpaceAsync(f, FloorName);
    var until = DateTime.UtcNow.AddMinutes(30);

    await f.Arming.SetAsync(space.id!, ArmMode.Armed, DanilName, default);
    await f.Arming.SetAsync(space.id!, ArmMode.Disarmed, DanilName, default, until);

    var row = await f.Db.ObjectArming.AsNoTracking()
      .SingleAsync(a => a.object_id == Guid.Parse(space.id!));
    Assert.False(row.armed);
    Assert.Equal(until, row.bypass_until);
    Assert.Equal(ArmMode.Armed, row.restore_mode);

    var view = await f.Arming.GetAsync(space.id!, default);
    Assert.Equal(until, view!.Arming!.BypassUntil);
  }

  [Fact]
  public async Task ATimedDisarmOfAFloorFollowingAnArmedBuildingRestoresInherit()
  {
    var f = await CreateAsync();
    var building = await NodeAsync(f.Map, BuildingName);
    var floor = await SpaceAsync(f, FloorName, building.id);
    await PlaceReferenceAsync(f, floor.id!);
    await f.Arming.SetAsync(building.id!, ArmMode.Armed, DanilName, default);
    var until = DateTime.UtcNow.AddMinutes(30);

    await f.Arming.SetAsync(floor.id!, ArmMode.Disarmed, DanilName, default, until);

    var row = await f.Db.ObjectArming.AsNoTracking()
      .SingleAsync(a => a.object_id == Guid.Parse(floor.id!));
    Assert.Equal(ArmMode.Inherit, row.restore_mode);

    var view = await f.Arming.GetAsync(floor.id!, default);
    Assert.True(view!.Arming!.Bypassed);
    Assert.Equal(building.id, view.Arming.SourceObjectId);
    Assert.Equal(until, view.Arming.BypassUntil);
  }

  [Fact]
  public async Task ExtendingATimedDisarmKeepsTheModeToRestore()
  {
    var f = await CreateAsync();
    var space = await SpaceAsync(f, FloorName);
    var first = DateTime.UtcNow.AddMinutes(30);
    var second = DateTime.UtcNow.AddMinutes(60);

    await f.Arming.SetAsync(space.id!, ArmMode.Armed, DanilName, default);
    await f.Arming.SetAsync(space.id!, ArmMode.Disarmed, DanilName, default, first);
    await f.Arming.SetAsync(space.id!, ArmMode.Disarmed, DanilName, default, second);

    var row = await f.Db.ObjectArming.AsNoTracking()
      .SingleAsync(a => a.object_id == Guid.Parse(space.id!));
    Assert.Equal(second, row.bypass_until);
    // The intent belongs to the first disarm: an armed room stays an armed room.
    Assert.Equal(ArmMode.Armed, row.restore_mode);
  }

  [Fact]
  public async Task AnExplicitModeCancelsTheDeadline()
  {
    var f = await CreateAsync();
    var space = await SpaceAsync(f, FloorName);

    await f.Arming.SetAsync(space.id!, ArmMode.Armed, DanilName, default);
    await f.Arming.SetAsync(
      space.id!, ArmMode.Disarmed, DanilName, default, DateTime.UtcNow.AddMinutes(30));
    await f.Arming.SetAsync(space.id!, ArmMode.Armed, DanilName, default);

    var row = await f.Db.ObjectArming.AsNoTracking()
      .SingleAsync(a => a.object_id == Guid.Parse(space.id!));
    Assert.True(row.armed);
    Assert.Null(row.bypass_until);
    Assert.Null(row.restore_mode);
  }

  [Fact]
  public async Task ADeadlineIsRefusedOnAnythingButADisarm()
  {
    var f = await CreateAsync();
    var space = await SpaceAsync(f, FloorName);
    var until = DateTime.UtcNow.AddMinutes(30);

    foreach (var mode in new[] { ArmMode.Armed, ArmMode.Inherit })
    {
      var refused = await Assert.ThrowsAsync<ArmingException>(() =>
        f.Arming.SetAsync(space.id!, mode, DanilName, default, until));
      Assert.Equal(ArmingRefusal.InvalidRequest, refused.Refusal);
    }

    Assert.Null(await StoredArmingAsync(f, space.id!));
  }

  [Fact]
  public async Task APastDeadlineIsRefused()
  {
    var f = await CreateAsync();
    var space = await SpaceAsync(f, FloorName);

    var refused = await Assert.ThrowsAsync<ArmingException>(() =>
      f.Arming.SetAsync(
        space.id!, ArmMode.Disarmed, DanilName, default, DateTime.UtcNow.AddMinutes(-1)));

    Assert.Equal(ArmingRefusal.InvalidRequest, refused.Refusal);
    Assert.Null(await StoredArmingAsync(f, space.id!));
  }

  [Fact]
  public async Task ATimedDisarmJournalsTheDeadline()
  {
    var f = await CreateAsync();
    var space = await SpaceAsync(f, FloorName);
    var until = DateTime.UtcNow.AddMinutes(30);

    await f.Arming.SetAsync(space.id!, ArmMode.Disarmed, DanilName, default, until);

    var entry = Assert.Single(await f.Db.Events.ToListAsync());
    Assert.Equal(NodeArmingService.JournalEventName, entry.event_name);
    Assert.Equal(ArmMode.Disarmed, entry.param1);
    Assert.Contains(entry.extra_props, p => p.prop_name == "until");
  }

  [Fact]
  public async Task ExtendingATimedDisarmDoesNotQueueARecheck()
  {
    var f = await CreateAsync();
    var space = await SpaceAsync(f, FloorName);

    await f.Arming.SetAsync(
      space.id!, ArmMode.Disarmed, DanilName, default, DateTime.UtcNow.AddMinutes(30));
    var queued = (await f.Db.MapOutbox.ToListAsync()).Count;

    await f.Arming.SetAsync(
      space.id!, ArmMode.Disarmed, DanilName, default, DateTime.UtcNow.AddMinutes(60));

    Assert.Equal(queued, (await f.Db.MapOutbox.ToListAsync()).Count);
    Assert.Equal(2, (await f.Db.Events.ToListAsync()).Count);
  }

  [Fact]
  public async Task ATimedDisarmOfAnInputStoresTheDeadline()
  {
    var f = await CreateAsync(armMode: ArmMode.Inherit);
    var until = DateTime.UtcNow.AddMinutes(30);

    await f.Arming.SetAsync(f.PartId, ArmMode.Disarmed, DanilName, default, until);

    var stored = await f.ObjectSettings.GetAsync(f.PartId, default);
    Assert.Equal(ArmMode.Disarmed, stored.Mode);
    Assert.Equal(until, stored.BypassUntil);
    Assert.Equal(ArmMode.Inherit, stored.RestoreMode);

    var view = await f.Arming.GetAsync(f.PartId, default);
    Assert.Equal(until, view!.Arming!.BypassUntil);
    Assert.False(view.Arming.Bypassed); // no zone above it, so it is simply off
  }

  [Fact]
  public async Task ATimedDisarmOfAnInputInAnArmedZoneIsBypassed()
  {
    var f = await CreateAsync(armMode: ArmMode.Inherit);
    var space = await SpaceAsync(f, FloorName);
    await PlaceReferenceAsync(f, space.id!);
    await f.Arming.SetAsync(space.id!, ArmMode.Armed, DanilName, default);
    var until = DateTime.UtcNow.AddMinutes(30);

    await f.Arming.SetAsync(f.PartId, ArmMode.Disarmed, DanilName, default, until);

    var view = await f.Arming.GetAsync(f.PartId, default);
    Assert.True(view!.Arming!.Bypassed);
    Assert.Equal(space.id, view.Arming.SourceObjectId);
    Assert.Equal(until, view.Arming.BypassUntil);
  }

  [Fact]
  public async Task ReturningAnInputToItsZoneClearsTheDeadline()
  {
    var f = await CreateAsync(armMode: ArmMode.Inherit);
    await f.Arming.SetAsync(
      f.PartId, ArmMode.Disarmed, DanilName, default, DateTime.UtcNow.AddMinutes(30));

    await f.Arming.SetAsync(f.PartId, ArmMode.Inherit, DanilName, default);

    var stored = await f.ObjectSettings.GetAsync(f.PartId, default);
    Assert.Equal(ArmMode.Inherit, stored.Mode);
    Assert.Null(stored.BypassUntil);
    Assert.Null(stored.RestoreMode);
  }

  [Fact]
  public async Task ReturningACameraToItsZoneUpdatesTheFlagInVmsCfg()
  {
    var f = await CreateAsync(cameraArmed: false, cameraMode: ArmMode.Disarmed);
    var space = await SpaceAsync(f, FloorName);
    await PlaceReferenceAsync(f, space.id!);
    await f.Arming.SetAsync(space.id!, ArmMode.Armed, DanilName, default);

    // `inherit` was the gap: the node path used to leave the flag alone, so the camera kept a
    // stale "disarmed" while the zone said otherwise.
    await f.Arming.SetAsync(CameraObjectId.For(CameraId), ArmMode.Inherit, DanilName, default);

    Assert.Contains((CameraId, true), f.Cfg.CameraFlags);
  }

  [Fact]
  public async Task AnUnknownNodeIsNotFound()
  {
    var f = await CreateAsync();

    Assert.Null(await f.Arming.GetAsync(Guid.NewGuid().ToString(), default));
    var refused = await Assert.ThrowsAsync<ArmingException>(() =>
      f.Arming.SetAsync(Guid.NewGuid().ToString(), ArmMode.Armed, DanilName, default));
    Assert.Equal(ArmingRefusal.NotFound, refused.Refusal);
  }

  [Fact]
  public async Task AnOperatorNameIsNotRequiredForTheJournal()
  {
    var f = await CreateAsync();
    var space = await SpaceAsync(f, FloorName);

    await f.Arming.SetAsync(space.id!, ArmMode.Armed, "  ", default);

    Assert.Equal(NodeArmingService.SystemActor,
      Assert.Single(await f.Db.Events.ToListAsync()).param0);
  }
}
