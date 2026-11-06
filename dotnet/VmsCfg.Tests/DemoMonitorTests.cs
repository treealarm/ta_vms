using VmsCfg.Demo;

namespace VmsCfg.Tests;

// The wall's plan is the part that decides what a second install writes; the rows themselves are
// the ordinary monitor tables. A camera the deployment lacks must be skipped, not invented.
public class DemoMonitorTests
{
  [Fact]
  public void TheWallShowsTheDemoCamerasInItsOwnOrder()
  {
    var plan = DemoMonitor.Plan([DemoCameraIds.For("sim-ptz2"), DemoCameraIds.For("sim-master")]);

    Assert.Equal([DemoCameraIds.For("sim-master"), DemoCameraIds.For("sim-ptz2")], plan.Cameras);
    Assert.Equal(["sim-master2", "sim-ptz"], plan.Skipped);
    Assert.Equal("2x2", plan.Layout);
    Assert.Equal("UMD DC", plan.Name);
  }

  [Fact]
  public void ASecondInstallPlansTheSameWallUnderTheSameId()
  {
    var all = new[] { "sim-master", "sim-master2", "sim-ptz", "sim-ptz2" }
      .Select(DemoCameraIds.For).ToList();
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
    Assert.Equal(4, plan.Skipped.Count);
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
