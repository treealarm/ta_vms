using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using VmsMap.Db;
using VmsMap.Domain;

namespace VmsMap.Tests;

// Rights are rows on tree nodes, inherited downwards; a replica also follows its owner. These
// tests run on the in-memory provider, so they exercise the walk itself, not a PostgreSQL CTE.
public class RightsServiceTests
{
  // The fixtures these tests share. The node names are labels the tests never assert; the users
  // and roles are the subjects the rights cases agree on.
  private const string BuildingName = "building";
  private const string FloorName = "floor";
  private const string CameraNodeName = "camera";
  private const string PlanName = "plan";
  private const string AliceName = "alice";
  private const string BobName = "bob";
  private const string AnyUserName = "anyone";
  private const string AdminRole = "admin";
  private const string OperatorsRole = "operators";
  private const string GuardsRole = "guards";

  private sealed record Services(VmsMapDbContext Db, MapDb Map, RightsService Rights);

  private static Services Create()
  {
    var options = new DbContextOptionsBuilder<VmsMapDbContext>()
      .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
      .Options;

    var db = new VmsMapDbContext(options);
    return new Services(db, new MapDb(db), new RightsService(db));
  }

  private static async Task<BaseMarkerDTO> Node(
    MapDb map, string name, string? parentId = null, string? ownerId = null)
  {
    var node = new BaseMarkerDTO { name = name, parent_id = parentId, owner_id = ownerId };
    await map.UpdateHierarchyAsync([node]);
    return node;
  }

  private static async Task Grant(
    VmsMapDbContext db, BaseMarkerDTO node, string subject, params string[] actions)
  {
    foreach (var action in actions)
    {
      db.Rights.Add(new DBRight
      {
        object_id = Guid.Parse(node.id!),
        subject = subject,
        action = action,
      });
    }

    await db.SaveChangesAsync();
  }

  private static CallerContext Caller(string? user = null, params string[] roles) =>
    new(user, roles, Unrestricted: false);

  private static Task<HashSet<string>> rightsAllowed(
    Services s, IReadOnlyCollection<string> ids, string action, CallerContext caller) =>
    s.Rights.AllowedAsync(ids, action, caller, default);

  [Fact]
  public async Task AViewGrantOnAnAncestorCoversTheWholeSubtree()
  {
    var s = Create();
    var building = await Node(s.Map, BuildingName);
    var floor = await Node(s.Map, FloorName, building.id);
    var camera = await Node(s.Map, CameraNodeName, floor.id);
    await Grant(s.Db, building, OperatorsRole, RightActions.View);

    var subtreeIds = new[] { building.id!, floor.id!, camera.id! };
    var visible = await s.Rights.VisibleAsync(
      subtreeIds, Caller(null, OperatorsRole), default);

    Assert.Equal(subtreeIds.Length, visible.Count);
    Assert.Empty(await s.Rights.VisibleAsync(
      subtreeIds, Caller(BobName), default));
  }

  [Fact]
  public async Task AReplicaFollowsItsOwner()
  {
    var s = Create();
    var camera = await Node(s.Map, CameraNodeName);
    var plan = await Node(s.Map, PlanName);
    var picture = await Node(s.Map, CameraNodeName, plan.id, ownerId: camera.id);
    await Grant(s.Db, camera, AliceName, RightActions.View);

    var visible = await s.Rights.VisibleAsync([picture.id!], Caller(AliceName), default);

    Assert.Contains(picture.id!, visible);
    Assert.Empty(await s.Rights.VisibleAsync([picture.id!], Caller(BobName), default));
  }

  [Fact]
  public async Task OpenModeMakesAnUnrightedNodeVisibleAndARowClosesIt()
  {
    var s = Create();
    var building = await Node(s.Map, BuildingName);
    var camera = await Node(s.Map, CameraNodeName, building.id);
    await s.Rights.SetModeAsync(RightsMode.Open, default);

    Assert.Contains(building.id!, await s.Rights.VisibleAsync([building.id!], Caller(BobName), default));

    await Grant(s.Db, building, AliceName, RightActions.View);

    Assert.Empty(await s.Rights.VisibleAsync([building.id!, camera.id!], Caller(BobName), default));
    Assert.Contains(camera.id!, await s.Rights.VisibleAsync([camera.id!], Caller(AliceName), default));
  }

  [Fact]
  public async Task AdminAndUnrestrictedBypassEveryRow()
  {
    var s = Create();
    var node = await Node(s.Map, BuildingName);

    Assert.Contains(node.id!, await s.Rights.VisibleAsync(
      [node.id!], Caller(AnyUserName, AdminRole), default));
    Assert.Contains(node.id!, await s.Rights.VisibleAsync(
      [node.id!], new CallerContext(AnyUserName, [], Unrestricted: true), default));
  }

  [Fact]
  public async Task IsAllowedChecksTheRequestedAction()
  {
    var s = Create();
    var camera = await Node(s.Map, CameraNodeName);
    await Grant(s.Db, camera, AliceName, RightActions.View, RightActions.ViewLive);

    Assert.True(await s.Rights.IsAllowedAsync(
      camera.id!, RightActions.ViewLive, Caller(AliceName), default));
    Assert.False(await s.Rights.IsAllowedAsync(
      camera.id!, RightActions.Ptz, Caller(AliceName), default));
  }

  [Fact]
  public async Task AcknowledgeIsInheritedLikeAnyOtherAction()
  {
    var s = Create();
    var building = await Node(s.Map, BuildingName);
    var floor = await Node(s.Map, FloorName, building.id);
    await Grant(s.Db, building, GuardsRole, RightActions.Acknowledge);

    // The batch form is the one a list uses; it must agree with the single check.
    var allowed = await rightsAllowed(s, [floor.id!], RightActions.Acknowledge, Caller(null, GuardsRole));
    Assert.Contains(floor.id!, allowed);

    Assert.True(await s.Rights.IsAllowedAsync(
      floor.id!, RightActions.Acknowledge, Caller(null, GuardsRole), default));
    Assert.False(await s.Rights.IsAllowedAsync(
      floor.id!, RightActions.Acknowledge, Caller(AliceName), default));
  }

  [Fact]
  public async Task AdminBypassesTheAcknowledgeRowsToo()
  {
    var s = Create();
    var node = await Node(s.Map, BuildingName);

    var allowed = await rightsAllowed(
      s, [node.id!], RightActions.Acknowledge, Caller(AnyUserName, AdminRole));
    Assert.Contains(node.id!, allowed);
  }

  [Fact]
  public async Task InOpenModeAcknowledgeFollowsTheSameSwitchAsView()
  {
    var s = Create();
    var node = await Node(s.Map, BuildingName);

    await s.Rights.SetModeAsync(RightsMode.Open, default);
    Assert.Contains(node.id!, await rightsAllowed(
      s, [node.id!], RightActions.Acknowledge, Caller(AliceName)));

    await s.Rights.SetModeAsync(RightsMode.Enforced, default);
    Assert.Empty(await rightsAllowed(s, [node.id!], RightActions.Acknowledge, Caller(AliceName)));
  }

  [Fact]
  public async Task EffectiveRightsExpandDownTheTreeAndNameTheCamera()
  {
    var s = Create();
    var building = await Node(s.Map, BuildingName);
    var camera = await Node(s.Map, CameraNodeName, building.id);
    s.Db.CameraPlacements.Add(new DBCameraPlacement
    {
      object_id = Guid.Parse(camera.id!),
      camera_id = TestCameras.One,
      is_reference = true,
      height_m = 3,
      hfov_deg = 60,
      range_m = 30,
    });
    await s.Db.SaveChangesAsync();
    await Grant(s.Db, building, OperatorsRole, RightActions.View, RightActions.ViewLive);

    var effective = await s.Rights.GetEffectiveAsync(Caller(null, OperatorsRole), default);

    const int rightedNodes = 2; // the building and the camera, one entry each
    Assert.Equal(rightedNodes, effective.Count);
    Assert.Contains(effective, n => n.object_id == camera.id && n.camera_id == TestCameras.One
      && n.actions.Contains(RightActions.ViewLive));
    Assert.Contains(effective, n => n.object_id == building.id && n.camera_id is null);
    Assert.Empty(await s.Rights.GetEffectiveAsync(Caller(BobName), default));
  }

  [Fact]
  public async Task EffectiveRightsIncludeReplicasOfARightedNode()
  {
    var s = Create();
    var camera = await Node(s.Map, CameraNodeName);
    var plan = await Node(s.Map, PlanName);
    var picture = await Node(s.Map, CameraNodeName, plan.id, ownerId: camera.id);
    await Grant(s.Db, camera, AliceName, RightActions.View);

    var effective = await s.Rights.GetEffectiveAsync(Caller(AliceName), default);

    Assert.Contains(effective, n => n.object_id == picture.id);
  }

  [Fact]
  public async Task ModeIsStoredAndStartsEnforced()
  {
    var s = Create();

    Assert.Equal(RightsMode.Enforced, await s.Rights.GetModeAsync(default));

    await s.Rights.SetModeAsync(RightsMode.Open, default);
    Assert.Equal(RightsMode.Open, await s.Rights.GetModeAsync(default));

    await s.Rights.SetModeAsync(RightsMode.Enforced, default);
    Assert.Equal(RightsMode.Enforced, await s.Rights.GetModeAsync(default));
  }
}
