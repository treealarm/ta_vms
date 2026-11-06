namespace VmsCfg.Demo;

/// <summary>
/// The camera side of the demo world: the video wall, the parts it configures and the reaction it
/// installs, in that order. Each step is idempotent by its own key, so a run that died after the
/// wall leaves the rest for the next one and a second run changes nothing.
/// </summary>
/// <remarks>
/// There is no transaction across the three: they are rows of different kinds in one database, and
/// a partial install is repaired by installing again. The order puts the wall first because the
/// subscription names its cameras, and the devices second because the sensor's arming is what makes
/// the contact an alarm.
/// </remarks>
public sealed class DemoInstaller(DemoMonitor monitor, DemoDevices devices, DemoRouting routing)
{
  public sealed record Installation(
    DemoMonitorPlan Monitor,
    DemoDevices.Plan Devices,
    DemoRouting.Plan Routing);

  public async Task<Installation> InstallAsync(CancellationToken ct)
  {
    // The wall answers which cameras the deployment has; the other two spend that answer rather
    // than asking the database a second time.
    var monitorPlan = await monitor.InstallAsync(ct);
    var present = monitorPlan.Cameras.ToHashSet(StringComparer.Ordinal);

    var devicePlan = await devices.InstallAsync(present, ct);
    var routingPlan = await routing.InstallAsync(present, ct);

    return new Installation(monitorPlan, devicePlan, routingPlan);
  }

  /// Takes the wall and the rule away. Cameras and their parts stay: they belong to the
  /// deployment, and a removal of the demo is not a removal of the hardware.
  public async Task<bool> RemoveAsync(CancellationToken ct)
  {
    var wall = await monitor.RemoveAsync(ct);
    var rule = await routing.RemoveAsync(ct);
    return wall || rule;
  }
}
