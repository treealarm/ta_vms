using VmsCfg.Demo;

namespace VmsCfg.Tests;

// The wall's plan is the part that decides what a second install writes; the rows themselves are
// the ordinary monitor tables. A camera the deployment lacks must be skipped, not invented.
public class DemoMonitorTests
{
  [Fact]
  public void TheWallShowsTheDemoCamerasInItsOwnOrder()
  {
    var plan = DemoMonitor.Plan(["sim-ptz2", "sim-master"]);

    Assert.Equal(["sim-master", "sim-ptz2"], plan.Cameras);
    Assert.Equal(["sim-master2", "sim-ptz"], plan.Skipped);
    Assert.Equal("2x2", plan.Layout);
    Assert.Equal("UMD DC", plan.Name);
  }

  [Fact]
  public void ASecondInstallPlansTheSameWallUnderTheSameId()
  {
    var first = DemoMonitor.Plan(["sim-master", "sim-master2", "sim-ptz", "sim-ptz2"]);
    var second = DemoMonitor.Plan(["sim-master", "sim-master2", "sim-ptz", "sim-ptz2"]);

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
