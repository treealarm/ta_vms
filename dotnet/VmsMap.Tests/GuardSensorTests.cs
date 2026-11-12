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

namespace VmsMap.Tests;

// The operator's guarding list: which sensors a space has, one page at a time, with the effective
// arming and only the rows the caller may see. The tests run on the in-memory provider, which has
// its own (LINQ) selection path; the relational one is the same query in SQL.
public class GuardSensorTests
{
  private const string CameraA = "11111111-1111-4111-8111-111111111111";
  private const string CameraB = "22222222-2222-4222-8222-222222222222";
  private const string CameraC = "33333333-3333-4333-8333-333333333333";

  private sealed record Fixture(
    VmsMapDbContext Db,
    MapDb Map,
    GuardSensorQuery Query,
    FakeSettings Settings,
    FakeCameraStates CameraStates,
    string SiteId,
    string FloorId,
    string OtherSiteId,
    string PartA,
    string PartB,
    string PartC);

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

    public Task<bool> GetArmedAsync(string cameraId, CancellationToken ct) =>
      Task.FromResult(Armed.GetValueOrDefault(cameraId));

    public Task<IReadOnlyDictionary<string, bool>> GetArmedManyAsync(
      IReadOnlyCollection<string> cameraIds, CancellationToken ct) =>
      Task.FromResult<IReadOnlyDictionary<string, bool>>(
        cameraIds.Distinct(StringComparer.Ordinal).ToDictionary(
          id => id, id => Armed.GetValueOrDefault(id), StringComparer.Ordinal));
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
    var updates = new MapUpdateService(map, tracks, states);
    var nodes = TestWiring.DeviceNodes(db, map, updates);
    var settings = new FakeSettings();
    var cameraStates = new FakeCameraStates();
    var rights = new RightsService(db);
    var query = new GuardSensorQuery(
      db, map, rights, settings, new EffectiveArmingResolver(db, settings, cameraStates));

    var site = await Node(map, "Site");
    var floor = await Node(map, "First floor", parentId: site.id);
    var otherSite = await Node(map, "Other site");

    // The names come from the settings at the moment the part's node is created, so they are
    // configured before the tree is. B has no row: an observed input nobody configured.
    settings.Rows.Add(new DeviceSettingsInfo(
      CameraA, DeviceObjectId.DigitalInputPart, "input", "Door", "inherit", "closed", true));
    settings.Rows.Add(new DeviceSettingsInfo(
      CameraC, DeviceObjectId.DigitalInputPart, "input", "Gate", "armed", "closed", true));

    // A stands on the floor, B directly on the site, C elsewhere.
    var partA = (await nodes.EnsureAsync(
      DeviceObjectId.CameraSource, CameraA, DeviceObjectId.DigitalInputPart, "input", default))!;
    var partB = (await nodes.EnsureAsync(
      DeviceObjectId.CameraSource, CameraB, DeviceObjectId.DigitalInputPart, "input", default))!;
    var partC = (await nodes.EnsureAsync(
      DeviceObjectId.CameraSource, CameraC, DeviceObjectId.DigitalInputPart, "input", default))!;

    await PlaceAsync(db, map, CameraA, floor.id!);
    await PlaceAsync(db, map, CameraB, site.id!);
    await PlaceAsync(db, map, CameraC, otherSite.id!);

    cameraStates.Armed[CameraA] = false;
    cameraStates.Armed[CameraB] = true;

    return new Fixture(
      db, map, query, settings, cameraStates, site.id!, floor.id!, otherSite.id!,
      partA, partB, partC);
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
      map, "Camera " + cameraId, parentId: spaceId, ownerId: CameraObjectId.For(cameraId));

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

  private static CallerContext Admin() => new("admin", ["admin"], Unrestricted: false);

  private static CallerContext User(params string[] roles) => new("olga", roles, Unrestricted: false);

  private static Task<GuardSensorPage> List(
    Fixture f, string? spaceId, int pageSize = 50, CallerContext? caller = null,
    string? afterName = null, string? afterId = null) =>
    f.Query.ListAsync(spaceId, pageSize, afterName, afterId, caller ?? Admin(), default);

  [Fact]
  public async Task ASpacesListCoversTheSpaceAndEverythingUnderIt()
  {
    var f = await CreateAsync();

    var page = await List(f, f.SiteId);

    // The part's display name is the node's name; the test wiring names it after its camera.
    Assert.Equal(
      new[] { $"Camera {CameraA} / input", $"Camera {CameraB} / input" },
      page.Rows.Select(r => r.Name).OrderBy(n => n, StringComparer.Ordinal));
    Assert.Equal(f.FloorId, page.Rows.Single(r => r.SourceId == CameraA).SpaceId);
    Assert.DoesNotContain(page.Rows, r => r.SourceId == CameraC);
  }

  [Fact]
  public async Task ANestedPlanHoldsOnlyWhatIsDrawnOnIt()
  {
    var f = await CreateAsync();

    var page = await List(f, f.FloorId);

    Assert.Single(page.Rows);
    Assert.Equal(CameraA, page.Rows[0].SourceId);
  }

  [Fact]
  public async Task ACameraPicturedInASpaceButStandingElsewhereIsNotListedThere()
  {
    var f = await CreateAsync();
    // A second, non-reference picture of C on the floor: a picture is not where the camera stands.
    await PlaceAsync(f.Db, f.Map, CameraC, f.FloorId, reference: false);

    var page = await List(f, f.FloorId);

    Assert.DoesNotContain(page.Rows, r => r.SourceId == CameraC);
  }

  [Fact]
  public async Task KeysetPagesCoverEveryRowOnce()
  {
    var f = await CreateAsync();
    var seen = new List<string>();
    string? afterName = null;
    string? afterId = null;

    for (var guard = 0; guard < 10; guard++)
    {
      var page = await List(f, null, pageSize: 1, afterName: afterName, afterId: afterId);
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

    var first = await List(f, null, pageSize: 2);
    var second = await List(
      f, null, pageSize: 2, afterName: first.NextName, afterId: first.NextId);

    Assert.Equal(2, first.Rows.Count);
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

    var page = await List(f, f.SiteId);
    var unconfigured = page.Rows.Single(r => r.SourceId == CameraB);

    Assert.False(unconfigured.Configured);
    Assert.Equal("inherit", unconfigured.ArmMode);
    Assert.True(unconfigured.Armed); // from the camera, since the site carries no armed property
    Assert.Equal(EffectiveArmingResolver.Camera, unconfigured.ArmingSource);
  }

  [Fact]
  public async Task TheEffectiveArmingComesFromTheSameSchemeAsTheCard()
  {
    var f = await CreateAsync();
    f.Db.Properties.Add(new DBMarkerProp
    {
      id = Guid.NewGuid(),
      object_id = Guid.Parse(f.SiteId),
      prop_name = EffectiveArmingResolver.ArmedProperty,
      str_val = "true",
    });
    await f.Db.SaveChangesAsync();

    var page = await List(f, f.SiteId);
    var sensor = page.Rows.Single(r => r.SourceId == CameraA);

    Assert.True(sensor.Armed);
    Assert.Equal(EffectiveArmingResolver.Space, sensor.ArmingSource);
  }

  [Fact]
  public async Task ARowTheCallerMayNotViewNeverLeavesTheService()
  {
    var f = await CreateAsync();
    // Enforced: only what is granted is visible. The site is granted to the role, the other
    // site is not, so C is filtered out.
    f.Db.RightsModes.Add(new DBRightsMode { id = true, mode = "enforced" });
    f.Db.Rights.Add(new DBRight
    {
      object_id = Guid.Parse(f.SiteId),
      subject = "operator",
      action = RightActions.ViewLive,
    });
    await f.Db.SaveChangesAsync();

    var page = await List(f, null, caller: User("operator"));

    Assert.Equal(
      new[] { CameraA, CameraB },
      page.Rows.Select(r => r.SourceId).OrderBy(id => id, StringComparer.Ordinal));
    Assert.All(page.Rows, r => Assert.False(r.CanControl));
  }

  [Fact]
  public async Task AControlGrantIsReportedPerRow()
  {
    var f = await CreateAsync();
    f.Db.RightsModes.Add(new DBRightsMode { id = true, mode = "enforced" });
    foreach (var action in new[] { RightActions.ViewLive, RightActions.Control })
    {
      f.Db.Rights.Add(new DBRight
      {
        object_id = Guid.Parse(f.SiteId),
        subject = "operator",
        action = action,
      });
    }
    await f.Db.SaveChangesAsync();

    var page = await List(f, f.FloorId, caller: User("operator"));

    Assert.Single(page.Rows);
    Assert.True(page.Rows[0].CanControl);
  }

  [Fact]
  public async Task AnEmptySpaceIsAnEmptyPageNotAnError()
  {
    var f = await CreateAsync();
    var page = await List(f, Guid.NewGuid().ToString());

    Assert.Empty(page.Rows);
    Assert.False(page.HasMore);
    Assert.Null(page.NextName);
  }

  [Fact]
  public async Task TheConfiguredModeAndAlarmFlagTravelWithTheRow()
  {
    var f = await CreateAsync();

    var page = await List(f, null);
    var a = page.Rows.Single(r => r.SourceId == CameraA);
    var c = page.Rows.Single(r => r.SourceId == CameraC);

    Assert.True(a.Configured);
    Assert.Equal("inherit", a.ArmMode);
    Assert.True(a.AlarmEnabled);
    Assert.True(c.Configured);
    Assert.True(c.Armed);
    Assert.Equal(EffectiveArmingResolver.Explicit, c.ArmingSource);
  }
}
