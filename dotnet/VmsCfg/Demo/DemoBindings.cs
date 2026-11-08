using Microsoft.Extensions.Logging;
using Vms.Cfg;

namespace VmsCfg.Demo;

/// <summary>
/// The two master/slave pairs of the demo: each fixed master's detections aim its PTZ slave
/// through the map, with no survey. The pairs are the world's, and the cameras are provisioned
/// with it, so installing the world is enough to make the demo work without the bench.
/// </summary>
/// <remarks>
/// The calibration behind the aiming is VmsMap's — the masters' ground marks and the slaves'
/// landmark observations travel with the demo world — and the map's geometry is what makes
/// `method = geo` meaningful. Both pairs follow people: the hall's walks indoors, the yard's once
/// they are outside, and neither pair has a car to follow any more.
/// </remarks>
public sealed class DemoBindings(CameraConfigService config, ILogger<DemoBindings> log)
{
  // Master, slave, and the object kinds the slave follows, in priority order.
  private static readonly (string Master, string Slave, string[] Kinds)[] Pairs =
  [
    ("sim-master", "sim-ptz", ["person"]),
    ("sim-master2", "sim-ptz2", ["person"]),
  ];

  /// <summary>The slaves that are now bound to a master, and the ones that are not.</summary>
  public sealed record Plan(IReadOnlyList<string> Bound, IReadOnlyList<string> Skipped);

  public async Task<Plan> InstallAsync(IReadOnlyCollection<string> present, CancellationToken ct)
  {
    var bound = new List<string>();
    var skipped = new List<string>();

    foreach (var (masterName, slaveName, kinds) in Pairs)
    {
      var master = DemoCameraIds.For(masterName);
      var slave = DemoCameraIds.For(slaveName);
      if (!present.Contains(master) || !present.Contains(slave))
      {
        skipped.Add(slaveName);
        continue;
      }

      // The binding writes the master onto the slave's policy in the same transaction, so the
      // policy next is what switches the following on.
      var binding = await config.ConfigurePtzSlaveBindingAsync(new PtzSlaveBinding
      {
        SlaveCameraId = slave,
        MasterCameraId = master,
        Method = PtzSlaveBindingMethod.PtzSlaveBindingGeo,
      }, ct);
      if (!binding.Success)
        throw new InvalidOperationException(
          $"The demo binding '{slaveName}' -> '{masterName}' was refused: {binding.Message}");

      var policyRow = new PtzTrackingPolicy
      {
        CameraId = slave,
        Enabled = true,
        Sources = { "onvif" },
      };
      policyRow.Kinds.AddRange(kinds);
      var policy = await config.ConfigurePtzTrackingAsync(policyRow, ct);
      if (!policy.Success)
        throw new InvalidOperationException(
          $"The demo policy of '{slaveName}' was refused: {policy.Message}");

      bound.Add(slaveName);
      log.LogInformation("Demo slave '{Slave}' follows '{Master}' through the map", slaveName, masterName);
    }

    return new Plan(bound, skipped);
  }

  /// <summary>
  /// Takes the bindings and the following away. The cameras stay — like the wall's, they belong to
  /// the deployment — and a slave with no binding keeps its policy row, switched off.
  /// </summary>
  public async Task RemoveAsync(CancellationToken ct)
  {
    foreach (var (_, slaveName, _) in Pairs)
    {
      var slave = DemoCameraIds.For(slaveName);
      await config.RemovePtzSlaveBindingAsync(slave, ct);
      await config.ConfigurePtzTrackingAsync(
        new PtzTrackingPolicy { CameraId = slave, Enabled = false }, ct);
    }
  }
}
