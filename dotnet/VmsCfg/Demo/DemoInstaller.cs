namespace VmsCfg.Demo;

/// <summary>
/// The camera side of the demo world: the cameras themselves, the video wall, the parts it
/// configures, the reaction it installs, and the two master/slave pairs that make the world move,
/// in that order. Each step is idempotent by its own key, so a run that died after the wall leaves
/// the rest for the next one and a second run changes nothing.
/// </summary>
/// <remarks>
/// There is no transaction across the steps: they are rows of different kinds in one database, and
/// a partial install is repaired by installing again. The order puts the cameras first because
/// every later step names them, and the wall next because the subscription names its cameras; the
/// devices follow because the sensor's arming is what makes the contact an alarm.
/// </remarks>
public sealed class DemoInstaller(
  DemoCameras cameras,
  DemoMonitor monitor,
  DemoDevices devices,
  DemoRouting routing,
  DemoBindings bindings)
{
  public sealed record Installation(
    DemoCameras.Result Cameras,
    DemoMonitorPlan Monitor,
    DemoDevices.Plan Devices,
    DemoRouting.Plan Routing,
    DemoBindings.Plan Bindings);

  public async Task<Installation> InstallAsync(CancellationToken ct)
  {
    // The cameras first: the wall answers which of them the deployment has, and the parts, the
    // reaction and the bindings all name them.
    var cameraPlan = await cameras.InstallAsync(ct);

    var monitorPlan = await monitor.InstallAsync(ct);
    var present = monitorPlan.Cameras.ToHashSet(StringComparer.Ordinal);

    var devicePlan = await devices.InstallAsync(present, ct);
    var routingPlan = await routing.InstallAsync(present, ct);
    var bindingPlan = await bindings.InstallAsync(present, ct);

    return new Installation(cameraPlan, monitorPlan, devicePlan, routingPlan, bindingPlan);
  }

  /// Takes the wall, the rule and the bindings away. Cameras and their parts stay: they belong to
  /// the deployment, and a removal of the demo is not a removal of the hardware.
  public async Task<bool> RemoveAsync(CancellationToken ct)
  {
    var wall = await monitor.RemoveAsync(ct);
    var rule = await routing.RemoveAsync(ct);
    await bindings.RemoveAsync(ct);
    return wall || rule;
  }
}
