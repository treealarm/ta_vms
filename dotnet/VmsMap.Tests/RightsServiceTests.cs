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

  [Fact]
  public async Task AViewGrantOnAnAncestorCoversTheWholeSubtree()
  {
    var s = Create();
    var building = await Node(s.Map, "Building");
    var floor = await Node(s.Map, "Floor", building.id);
    var camera = await Node(s.Map, "Camera", floor.id);
    await Grant(s.Db, building, "operators", RightActions.View);

    var visible = await s.Rights.VisibleAsync(
      [building.id!, floor.id!, camera.id!], Caller(null, "operators"), default);

    Assert.Equal(3, visible.Count);
    Assert.Empty(await s.Rights.VisibleAsync(
      [building.id!, floor.id!, camera.id!], Caller("bob"), default));
  }

  [Fact]
  public async Task AReplicaFollowsItsOwner()
  {
    var s = Create();
    var camera = await Node(s.Map, "Camera");
    var plan = await Node(s.Map, "Plan");
    var picture = await Node(s.Map, "Camera", plan.id, ownerId: camera.id);
    await Grant(s.Db, camera, "alice", RightActions.View);

    var visible = await s.Rights.VisibleAsync([picture.id!], Caller("alice"), default);

    Assert.Contains(picture.id!, visible);
    Assert.Empty(await s.Rights.VisibleAsync([picture.id!], Caller("bob"), default));
  }

  [Fact]
  public async Task OpenModeMakesAnUnrightedNodeVisibleAndARowClosesIt()
  {
    var s = Create();
    var building = await Node(s.Map, "Building");
    var camera = await Node(s.Map, "Camera", building.id);
    await s.Rights.SetModeAsync(RightsMode.Open, default);

    Assert.Contains(building.id!, await s.Rights.VisibleAsync([building.id!], Caller("bob"), default));

    await Grant(s.Db, building, "alice", RightActions.View);

    Assert.Empty(await s.Rights.VisibleAsync([building.id!, camera.id!], Caller("bob"), default));
    Assert.Contains(camera.id!, await s.Rights.VisibleAsync([camera.id!], Caller("alice"), default));
  }

  [Fact]
  public async Task AdminAndUnrestrictedBypassEveryRow()
  {
    var s = Create();
    var node = await Node(s.Map, "Building");

    Assert.Contains(node.id!, await s.Rights.VisibleAsync(
      [node.id!], Caller("anyone", "admin"), default));
    Assert.Contains(node.id!, await s.Rights.VisibleAsync(
      [node.id!], new CallerContext("anyone", [], Unrestricted: true), default));
  }

  [Fact]
  public async Task IsAllowedChecksTheRequestedAction()
  {
    var s = Create();
    var camera = await Node(s.Map, "Camera");
    await Grant(s.Db, camera, "alice", RightActions.View, RightActions.ViewLive);

    Assert.True(await s.Rights.IsAllowedAsync(
      camera.id!, RightActions.ViewLive, Caller("alice"), default));
    Assert.False(await s.Rights.IsAllowedAsync(
      camera.id!, RightActions.Ptz, Caller("alice"), default));
  }

  [Fact]
  public async Task EffectiveRightsExpandDownTheTreeAndNameTheCamera()
  {
    var s = Create();
    var building = await Node(s.Map, "Building");
    var camera = await Node(s.Map, "Camera", building.id);
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
    await Grant(s.Db, building, "operators", RightActions.View, RightActions.ViewLive);

    var effective = await s.Rights.GetEffectiveAsync(Caller(null, "operators"), default);

    Assert.Equal(2, effective.Count);
    Assert.Contains(effective, n => n.object_id == camera.id && n.camera_id == TestCameras.One
      && n.actions.Contains(RightActions.ViewLive));
    Assert.Contains(effective, n => n.object_id == building.id && n.camera_id is null);
    Assert.Empty(await s.Rights.GetEffectiveAsync(Caller("bob"), default));
  }

  [Fact]
  public async Task EffectiveRightsIncludeReplicasOfARightedNode()
  {
    var s = Create();
    var camera = await Node(s.Map, "Camera");
    var plan = await Node(s.Map, "Plan");
    var picture = await Node(s.Map, "Camera", plan.id, ownerId: camera.id);
    await Grant(s.Db, camera, "alice", RightActions.View);

    var effective = await s.Rights.GetEffectiveAsync(Caller("alice"), default);

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
