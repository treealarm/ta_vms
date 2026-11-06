using VmsCfg.Demo;

namespace VmsCfg.Tests;

// The wall's plan is the part that decides what a second install writes; the rows themselves are
// the ordinary monitor tables. A camera the deployment lacks must be skipped, not invented.
public class DemoMonitorTests
{
  private static readonly string[] DemoCameras =
    ["sim-master", "sim-master2", "sim-ptz", "sim-ptz2", "sim-vehicle"];

  [Fact]
  public void TheWallShowsTheDemoCamerasInItsOwnOrder()
  {
    var plan = DemoMonitor.Plan([DemoCameraIds.For("sim-ptz2"), DemoCameraIds.For("sim-master")]);

    Assert.Equal([DemoCameraIds.For("sim-master"), DemoCameraIds.For("sim-ptz2")], plan.Cameras);
    Assert.Equal(["sim-master2", "sim-ptz", "sim-vehicle"], plan.Skipped);
    Assert.Equal("1+5", plan.Layout);
    Assert.Equal("UMD DC", plan.Name);
  }

  [Fact]
  public void TheFiveCamerasFillTheWallInTheOrderItShowsThem()
  {
    var plan = DemoMonitor.Plan(DemoCameras.Select(DemoCameraIds.For));

    Assert.Equal(DemoCameras.Select(DemoCameraIds.For), plan.Cameras);
    Assert.Empty(plan.Skipped);
  }

  [Fact]
  public void AWorldWithoutTheVehicleCameraIsStillAWall()
  {
    // sim-vehicle is a moving camera, not one of the demo's devices: a deployment that has not
    // provisioned it yet still gets the four that stand in the world.
    var plan = DemoMonitor.Plan(DemoCameras.Where(name => name != "sim-vehicle").Select(DemoCameraIds.For));

    Assert.Equal(new[] { "sim-master", "sim-master2", "sim-ptz", "sim-ptz2" }.Select(DemoCameraIds.For), plan.Cameras);
    Assert.Equal(["sim-vehicle"], plan.Skipped);
  }

  [Fact]
  public void ASecondInstallPlansTheSameWallUnderTheSameId()
  {
    var all = DemoCameras.Select(DemoCameraIds.For).ToList();
    var first = DemoMonitor.Plan(all);
    var second = DemoMonitor.Plan(all);

    Assert.Equal(first.MonitorId, second.MonitorId);
    Assert.Equal(DemoMonitor.MonitorId.ToString(), first.MonitorId);
    Assert.Equal(first.Cameras, second.Cameras);
    Assert.Empty(first.Skipped);
    Assert.Empty(second.Skipped);
  }

  [Fact]
  public void AWorldWithoutCamerasIsValid()
  {
    var plan = DemoMonitor.Plan([]);

    Assert.Empty(plan.Cameras);
    Assert.Equal(5, plan.Skipped.Count);
  }
}

// The id derivation is a contract with VmsMap's DemoIds, which places the very same cameras; the
// two services have no shared project, so the value is pinned on both sides.
public class DemoCameraIdsTests
{
  [Fact]
  public void TheIdIsTheOneTheWorldDerives()
  {
    Assert.Equal("94681efe-57da-5093-b420-1bed3b28dae8", DemoCameraIds.For("sim-master"));
  }
}
