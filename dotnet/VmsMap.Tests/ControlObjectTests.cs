using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;
using VmsMap.Clients;
using VmsMap.Db;
using VmsMap.Domain;
using VmsMap.Domain.Arming;
using VmsMap.Domain.Control;

namespace VmsMap.Tests;

// The operator's control list: which objects a space holds — cameras, their sensors and their
// relays — one page at a time, with the effective arming, the live state and only the rows the
// caller may see. The tests run on the in-memory provider, which has its own (LINQ) selection
// path; the relational one is the same selection in SQL.
public class ControlObjectTests
{
  private const string CameraA = "11111111-1111-4111-8111-111111111111";
  private const string CameraB = "22222222-2222-4222-8222-222222222222";
  private const string CameraC = "33333333-3333-4333-8333-333333333333";

  // The fixtures these tests share.
  private const string CameraNamePrefix = "Camera ";
  private const string DigitalInputPart = "digital_input";
  private const string InputToken = "input";
  private const string RelayPart = "relay";
  private const string InheritMode = "inherit";
  private const string EnforcedMode = "enforced";
  private const string ClosedTrigger = "closed";
  private const string CameraKind = "camera";
  private const string AdminRole = "admin";
  private const string OperatorRole = "operator";
  private const string PictureName = "door picture";
  private const int CameraCount = 3;

  private sealed record Fixture(
    VmsMapDbContext Db,
    MapDb Map,
    ControlObjectQuery Query,
    FakeSettings Settings,
    FakeCameraStates CameraStates,
    string SiteId,
    string FloorId,
    string OtherSiteId,
    string PartA,
    string PartB,
    string PartC,
    string RelayA);

  private sealed class FakeSettings : IDeviceSettingsSource
  {
    public List<DeviceSettingsInfo> Rows { get; } = [];

    public Task<IReadOnlyList<DeviceSettingsInfo>> ListAsync(string cameraId, CancellationToken ct) =>
      Task.FromResult<IReadOnlyList<DeviceSettingsInfo>>(
        Rows.Where(r => r.CameraId == cameraId).ToList());

    public Task<DeviceSettingsInfo?> FindAsync(
      string cameraId, string kind, string token, CancellationToken ct) =>
      Task.FromResult(Rows.FirstOrDefault(
        r => r.CameraId == cameraId && r.Kind == kind && r.Token == token));

    public Task<IReadOnlyDictionary<string, IReadOnlyList<DeviceSettingsInfo>>> ListManyAsync(
      IReadOnlyCollection<string> cameraIds, CancellationToken ct) =>
      Task.FromResult<IReadOnlyDictionary<string, IReadOnlyList<DeviceSettingsInfo>>>(
        cameraIds.Distinct(StringComparer.Ordinal).ToDictionary(
          id => id,
          id => (IReadOnlyList<DeviceSettingsInfo>)Rows.Where(r => r.CameraId == id).ToList(),
          StringComparer.Ordinal));

    public void Invalidate(string cameraId) { }
  }

  private sealed class FakeCameraStates : ICameraStateSource
  {
    public Dictionary<string, bool> Armed { get; } = new(StringComparer.Ordinal);

    public Task<CameraArmState?> GetStateAsync(string cameraId, CancellationToken ct) =>
      Task.FromResult<CameraArmState?>(State(cameraId));

    public Task<IReadOnlyDictionary<string, CameraArmState>> GetStatesAsync(
      IReadOnlyCollection<string> cameraIds, CancellationToken ct) =>
      Task.FromResult<IReadOnlyDictionary<string, CameraArmState>>(
        cameraIds.Distinct(StringComparer.Ordinal)
          .ToDictionary(id => id, State, StringComparer.Ordinal));

    private CameraArmState State(string cameraId) =>
      new(cameraId, Armed.GetValueOrDefault(cameraId), InheritMode);
  }

  private static async Task<Fixture> CreateAsync()
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
    var settings = new FakeSettings();
    var objectSettings = TestWiring.ObjectSettings(db);
    var cameraStates = new FakeCameraStates();
    var rights = new RightsService(db);
    var query = new ControlObjectQuery(
      db, map, rights, settings, objectSettings, cameraStates,
      new EffectiveArmingResolver(db, objectSettings, cameraStates));

    var site = await Node(map, "site");
    var floor = await Node(map, "first floor", parentId: site.id);
    var otherSite = await Node(map, "other site");

    // The names come from the settings at the moment the part's node is created, so they are
    // configured before the tree is. B has no row: an observed input nobody configured.
    settings.Rows.Add(new DeviceSettingsInfo(
      CameraA, DeviceObjectId.DigitalInputPart, InputToken, "door", InheritMode, ClosedTrigger, true));
    settings.Rows.Add(new DeviceSettingsInfo(
      CameraC, DeviceObjectId.DigitalInputPart, InputToken, "gate", "armed", ClosedTrigger, true));

    // A stands on the floor, B directly on the site, C elsewhere. A also declares a relay.
    var partA = (await nodes.EnsureAsync(
      DeviceObjectId.CameraSource, CameraA, DeviceObjectId.DigitalInputPart, InputToken, default))!;
    var partB = (await nodes.EnsureAsync(
      DeviceObjectId.CameraSource, CameraB, DeviceObjectId.DigitalInputPart, InputToken, default))!;
    var partC = (await nodes.EnsureAsync(
      DeviceObjectId.CameraSource, CameraC, DeviceObjectId.DigitalInputPart, InputToken, default))!;
    var relayA = (await nodes.EnsureAsync(
      DeviceObjectId.CameraSource, CameraA, RelayPart, "lamp", default))!;

    await PlaceAsync(db, map, CameraA, floor.id!);
    await PlaceAsync(db, map, CameraB, site.id!);
    await PlaceAsync(db, map, CameraC, otherSite.id!);

    // What the parts report right now: A's door is closed, A's lamp is active.
    await StateAsync(db, partA, "SENSOR_CLOSED");
    await StateAsync(db, relayA, "RELAY_ACTIVE");

    // The modes live in the map, keyed by the node; the flags are VmsCfg's.
    await objectSettings.SetAsync(
      CameraObjectId.For(CameraA), ArmMode.Disarmed, null, false, default);
    await objectSettings.SetAsync(
      CameraObjectId.For(CameraB), ArmMode.Armed, null, false, default);
    await objectSettings.SetAsync(partA, ArmMode.Inherit, ClosedTrigger, true, default);
    await objectSettings.SetAsync(partC, ArmMode.Armed, ClosedTrigger, true, default);
    cameraStates.Armed[CameraA] = false;
    cameraStates.Armed[CameraB] = true;

    return new Fixture(
      db, map, query, settings, cameraStates, site.id!, floor.id!, otherSite.id!,
      partA, partB, partC, relayA);
  }

  private static async Task<BaseMarkerDTO> Node(
    MapDb map, string name, string? parentId = null, string? ownerId = null)
  {
    var node = new BaseMarkerDTO
    {
      id = Guid.NewGuid().ToString(), name = name, parent_id = parentId, owner_id = ownerId,
    };
    await map.UpdateHierarchyAsync([node]);
    return node;
  }

  /// One placement of a camera: the reference by default, or an ordinary picture when asked.
  private static async Task PlaceAsync(
    VmsMapDbContext db, MapDb map, string cameraId, string spaceId, bool reference = true)
  {
    var replica = await Node(
      map, CameraNamePrefix + cameraId, parentId: spaceId, ownerId: CameraObjectId.For(cameraId));

    db.CameraPlacements.Add(new DBCameraPlacement
    {
      object_id = Guid.Parse(replica.id!),
      camera_id = cameraId,
      is_reference = reference,
    });
    db.GeoObjects.Add(new DBGeoObject
    {
      id = Guid.Parse(replica.id!),
      figure = new Point(0, 0),
      canvas_id = Guid.Parse(spaceId),
    });
    await db.SaveChangesAsync();
  }

  private static async Task StateAsync(VmsMapDbContext db, string nodeId, string state)
  {
    var id = Guid.Parse(nodeId);
    db.ObjectStates.Add(new DBObjectState
    {
      id = id,
      timestamp = DateTime.UtcNow,
      states = [new DBObjectStateValue { id = Guid.NewGuid(), object_id = id, state = state }],
    });
    await db.SaveChangesAsync();
  }

  private static CallerContext Admin() => new(AdminRole, [AdminRole], Unrestricted: false);

  private static CallerContext User(params string[] roles) => new("olga", roles, Unrestricted: false);

  private static Task<ControlObjectPage> List(
    Fixture f, string? spaceId, string? kind = null, string? cameraId = null, int pageSize = 50,
    CallerContext? caller = null, string? afterName = null, string? afterId = null) =>
    f.Query.ListAsync(
      spaceId, kind, cameraId, pageSize, afterName, afterId, caller ?? Admin(), default);

  private static Task<ControlObjectRow?> Find(
    Fixture f, string nodeId, CallerContext? caller = null) =>
    f.Query.FindAsync(nodeId, caller ?? Admin(), default);

  /// The name the test wiring gives a camera's part node: the camera's, then the part's token.
  private static string PartNodeName(string cameraId) =>
    $"{CameraNamePrefix}{cameraId} / {InputToken}";

  [Fact]
  public async Task ASpacesSensorsCoverTheSpaceAndEverythingUnderIt()
  {
    var f = await CreateAsync();

    var page = await List(f, f.SiteId, kind: DigitalInputPart);

    // The part's display name is the node's name; the test wiring names it after its camera.
    Assert.Equal(
      new[] { PartNodeName(CameraA), PartNodeName(CameraB) },
      page.Rows.Select(r => r.Name).OrderBy(n => n, StringComparer.Ordinal));
    Assert.Equal(f.FloorId, page.Rows.Single(r => r.SourceId == CameraA).SpaceId);
    Assert.DoesNotContain(page.Rows, r => r.SourceId == CameraC);
  }

  [Fact]
  public async Task ANestedPlanHoldsOnlyWhatIsDrawnOnIt()
  {
    var f = await CreateAsync();

    var page = await List(f, f.FloorId, kind: DigitalInputPart);

    Assert.Equal(CameraA, Assert.Single(page.Rows).SourceId);
  }

  [Fact]
  public async Task ACameraPicturedInASpaceButStandingElsewhereIsNotListedThere()
  {
    var f = await CreateAsync();
    // A second, non-reference picture of C on the floor: a picture is not where the camera stands.
    await PlaceAsync(f.Db, f.Map, CameraC, f.FloorId, reference: false);

    var page = await List(f, f.FloorId, kind: DigitalInputPart);

    Assert.DoesNotContain(page.Rows, r => r.SourceId == CameraC);
  }

  [Fact]
  public async Task KeysetPagesCoverEveryRowOnce()
  {
    var f = await CreateAsync();
    var seen = new List<string>();
    string? afterName = null;
    string? afterId = null;

    const int rowsPerPage = 1;
    const int maxPages = 10;
    for (var guard = 0; guard < maxPages; guard++)
    {
      var page = await List(
        f, null, kind: DigitalInputPart, pageSize: rowsPerPage, afterName: afterName, afterId: afterId);
      seen.AddRange(page.Rows.Select(r => r.ObjectId));
      if (!page.HasMore)
        break;
      afterName = page.NextName;
      afterId = page.NextId;
    }

    Assert.Equal(
      new[] { f.PartA, f.PartB, f.PartC }.OrderBy(id => id, StringComparer.Ordinal),
      seen.OrderBy(id => id, StringComparer.Ordinal));
    Assert.Equal(seen.Count, seen.Distinct().Count());
  }

  // A page that fills in the middle of a fetched chunk reads one row past its limit; that row is
  // not hidden, it is simply not read yet. The cursor has to stop where the page stopped.
  [Fact]
  public async Task APageThatFillsMidChunkDoesNotDropTheRowBehindIt()
  {
    var f = await CreateAsync();

    const int rowsPerPage = 2;
    var first = await List(f, null, kind: DigitalInputPart, pageSize: rowsPerPage);
    var second = await List(
      f, null, kind: DigitalInputPart, pageSize: rowsPerPage,
      afterName: first.NextName, afterId: first.NextId);

    Assert.Equal(rowsPerPage, first.Rows.Count);
    Assert.True(first.HasMore);
    Assert.Single(second.Rows);
    Assert.False(second.HasMore);

    var ids = first.Rows.Select(r => r.ObjectId).Concat(second.Rows.Select(r => r.ObjectId));
    Assert.Equal(
      new[] { f.PartA, f.PartB, f.PartC }.OrderBy(id => id, StringComparer.Ordinal),
      ids.OrderBy(id => id, StringComparer.Ordinal));
  }

  [Fact]
  public async Task ARowWithoutSettingsIsShownButCannotBeArmed()
  {
    var f = await CreateAsync();

    var page = await List(f, f.SiteId, kind: DigitalInputPart);
    var unconfigured = page.Rows.Single(r => r.SourceId == CameraB);

    Assert.False(unconfigured.Configured);
    Assert.Equal(InheritMode, unconfigured.ArmMode);
    Assert.True(unconfigured.Armed); // from the camera, since the site carries no armed property
    Assert.Equal(EffectiveArmingResolver.Camera, unconfigured.ArmingSource);
    Assert.True(unconfigured.HasArming);
  }

  [Fact]
  public async Task TheEffectiveArmingComesFromTheSameSchemeAsTheCard()
  {
    var f = await CreateAsync();
    f.Db.ObjectArming.Add(new DBObjectArming
    {
      object_id = Guid.Parse(f.SiteId),
      armed = true,
    });
    await f.Db.SaveChangesAsync();

    var page = await List(f, f.SiteId, kind: DigitalInputPart);
    var sensor = page.Rows.Single(r => r.SourceId == CameraA);

    Assert.True(sensor.Armed);
    Assert.Equal(EffectiveArmingResolver.Space, sensor.ArmingSource);
  }

  [Fact]
  public async Task ABypassedRowNamesTheZoneItIsExcludedFrom()
  {
    var f = await CreateAsync();
    // The floor is armed; the input's own mode is disarmed, so it is excluded rather than off.
    f.Db.ObjectArming.Add(new DBObjectArming
    {
      object_id = Guid.Parse(f.FloorId),
      armed = true,
    });
    var settings = await f.Db.DeviceObjectSettings.SingleAsync(
      s => s.object_id == Guid.Parse(f.PartA));
    settings.arm_mode = ArmMode.Disarmed;
    await f.Db.SaveChangesAsync();

    var row = await Find(f, f.PartA);

    Assert.False(row!.Armed);
    Assert.True(row.Bypassed);
    Assert.Equal(EffectiveArmingResolver.Explicit, row.ArmingSource);
    Assert.Equal(f.FloorId, row.ArmingSourceObjectId);
  }

  [Fact]
  public async Task ARowTheCallerMayNotViewNeverLeavesTheService()
  {
    var f = await CreateAsync();
    // Enforced: only what is granted is visible. The site is granted to the role, the other
    // site is not, so C is filtered out.
    f.Db.RightsModes.Add(new DBRightsMode { id = true, mode = EnforcedMode });
    f.Db.Rights.Add(new DBRight
    {
      object_id = Guid.Parse(f.SiteId),
      subject = OperatorRole,
      action = RightActions.ViewLive,
    });
    await f.Db.SaveChangesAsync();

    var page = await List(f, null, kind: DigitalInputPart, caller: User(OperatorRole));

    Assert.Equal(
      new[] { CameraA, CameraB },
      page.Rows.Select(r => r.SourceId).OrderBy(id => id, StringComparer.Ordinal));
    Assert.All(page.Rows, r => Assert.False(r.CanControl));
  }

  [Fact]
  public async Task AControlGrantIsReportedPerRow()
  {
    var f = await CreateAsync();
    f.Db.RightsModes.Add(new DBRightsMode { id = true, mode = EnforcedMode });
    foreach (var action in new[] { RightActions.ViewLive, RightActions.Control })
    {
      f.Db.Rights.Add(new DBRight
      {
        object_id = Guid.Parse(f.SiteId),
        subject = OperatorRole,
        action = action,
      });
    }
    await f.Db.SaveChangesAsync();

    var page = await List(f, f.FloorId, kind: DigitalInputPart, caller: User(OperatorRole));

    Assert.True(Assert.Single(page.Rows).CanControl);
  }

  [Fact]
  public async Task AnEmptySpaceIsAnEmptyPageNotAnError()
  {
    var f = await CreateAsync();
    var page = await List(f, Guid.NewGuid().ToString(), kind: DigitalInputPart);

    Assert.Empty(page.Rows);
    Assert.False(page.HasMore);
    Assert.Null(page.NextName);
  }

  [Fact]
  public async Task TheConfiguredModeAndAlarmFlagTravelWithTheRow()
  {
    var f = await CreateAsync();

    var page = await List(f, null, kind: DigitalInputPart);
    var a = page.Rows.Single(r => r.SourceId == CameraA);
    var c = page.Rows.Single(r => r.SourceId == CameraC);

    Assert.True(a.Configured);
    Assert.Equal(InheritMode, a.ArmMode);
    Assert.True(a.AlarmEnabled);
    Assert.True(c.Configured);
    Assert.True(c.Armed);
    Assert.Equal(EffectiveArmingResolver.Explicit, c.ArmingSource);
  }

  // A camera is an object of its own in the list: what it says is its own flag, not what a zone
  // would make its sensors inherit.
  [Fact]
  public async Task ACameraRowCarriesItsOwnFlag()
  {
    var f = await CreateAsync();

    var page = await List(f, null, kind: CameraKind);

    Assert.Equal(CameraCount, page.Rows.Count);
    var a = page.Rows.Single(r => r.SourceId == CameraA);
    var b = page.Rows.Single(r => r.SourceId == CameraB);
    var c = page.Rows.Single(r => r.SourceId == CameraC);

    Assert.Equal(CameraKind, a.Kind);
    Assert.True(a.HasArming);
    Assert.False(a.Armed);
    Assert.True(b.Armed);
    Assert.False(c.Armed);
    Assert.Equal(EffectiveArmingResolver.Explicit, a.ArmingSource);
    Assert.Equal("disarmed", a.ArmMode);
    Assert.True(a.CanControl);
  }

  // A relay is driven, not watched: no guard, a live state, and a toggle is the whole action.
  [Fact]
  public async Task ARelayRowIsSwitchedButNeverArmed()
  {
    var f = await CreateAsync();

    var page = await List(f, null, kind: RelayPart);

    var relay = Assert.Single(page.Rows);
    Assert.Equal(f.RelayA, relay.ObjectId);
    Assert.Equal(RelayPart, relay.Kind);
    Assert.False(relay.HasArming);
    Assert.Equal("active", relay.State);
    Assert.Equal(string.Empty, relay.ArmMode);
    Assert.True(relay.CanControl);
  }

  [Fact]
  public async Task ALiveStateComesFromTheOwner()
  {
    var f = await CreateAsync();

    var page = await List(f, null, kind: DigitalInputPart);

    Assert.Equal(ClosedTrigger, page.Rows.Single(r => r.SourceId == CameraA).State);
    Assert.Equal(string.Empty, page.Rows.Single(r => r.SourceId == CameraB).State);
  }

  [Fact]
  public async Task ACameraFilterSelectsThatDevicesObjects()
  {
    var f = await CreateAsync();

    var page = await List(f, null, cameraId: CameraA);

    Assert.Equal(CameraCount, page.Rows.Count);
    Assert.All(page.Rows, r => Assert.Equal(CameraA, r.SourceId));
  }

  [Fact]
  public async Task AnUnknownKindIsRejected()
  {
    Assert.Throws<ArgumentException>(() => ControlObjectQuery.ParseKind("door"));
    Assert.Null(ControlObjectQuery.ParseKind(""));
    Assert.Equal(new[] { RelayPart }, ControlObjectQuery.ParseKind(RelayPart)!);
  }

  // A node selected on a map may be the picture rather than the object: the read resolves it
  // through its owner and answers with the object's own row.
  [Fact]
  public async Task AReplicaResolvesToTheObjectItShows()
  {
    var f = await CreateAsync();
    var picture = await Node(
      f.Map, PictureName, parentId: f.FloorId, ownerId: f.PartA);

    var row = await Find(f, picture.id!);

    Assert.NotNull(row);
    Assert.Equal(f.PartA, row!.ObjectId);
    Assert.Equal(PartNodeName(CameraA), row.Name);
    Assert.Equal(f.FloorId, row.SpaceId);
    Assert.False(row.Armed); // A's camera flag; no zone property on the floor
    Assert.Equal(EffectiveArmingResolver.Camera, row.ArmingSource);
    Assert.Equal(ClosedTrigger, row.State);
    Assert.True(row.CanControl);
  }

  [Fact]
  public async Task AnObjectThatIsNotADeviceIsNotFound()
  {
    var f = await CreateAsync();

    Assert.Null(await Find(f, f.FloorId));
    Assert.Null(await Find(f, Guid.NewGuid().ToString()));
    Assert.Null(await Find(f, "not-a-uuid"));
  }

  [Fact]
  public async Task AReplicaTheCallerMayNotSeeIsNotFound()
  {
    var f = await CreateAsync();
    var picture = await Node(f.Map, PictureName, parentId: f.FloorId, ownerId: f.PartA);
    f.Db.RightsModes.Add(new DBRightsMode { id = true, mode = EnforcedMode });
    f.Db.Rights.Add(new DBRight
    {
      object_id = Guid.Parse(f.OtherSiteId),
      subject = OperatorRole,
      action = RightActions.ViewLive,
    });
    await f.Db.SaveChangesAsync();

    Assert.Null(await Find(f, picture.id!, User(OperatorRole)));
  }
}
