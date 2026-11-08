using Vms.Cfg;

namespace VmsCfg.Demo;

/// The parts the demo configures. A part's name and arming are configuration like the wall, so they
/// belong to VmsCfg's half of the demo, not to web_srv: the install is the same whether an
/// administrator pressed the button or `DEMO_CONFIG` installed the world at startup.
public sealed class DemoDevices(DeviceStorage devices, DeviceSettingsNotifier notifier)
{
  /// What one install did, in the names the operator is told: parts it configured, cameras it could
  /// not because the deployment does not have them yet.
  public sealed record Plan(IReadOnlyList<string> Configured, IReadOnlyList<string> Skipped);

  /// The contact that means "the relay fired", shared with the demo's routing rule so the arming and
  /// the rule cannot drift apart.
  /// <para>
  /// `sim-ptz`'s input is wired to `sim-master`'s `peer` relay. The input's idle state is open, so
  /// an open circuit is idle and a closed one is active: that contact is closed. It is the polarity
  /// the trigger is set to, not a guess from the picture.
  /// </para>
  public const string SensorTrigger = DeviceSettingsRules.TriggerClosed;

  public static DeviceSettings SensorSettings() => new()
  {
    CameraId = DemoCameraIds.For("sim-ptz"),
    Kind = DeviceSettingsRules.DigitalInput,
    Token = "input",
    Name = "Sensor",
    ArmMode = DeviceSettingsRules.Armed,
    TriggerOn = SensorTrigger,
    AlarmEnabled = true,
  };

  /// The parts of the master. They are configured — not merely discovered — because a device's
  /// diagram is drawn only from parts an operator set up, and the demo is where that rule is
  /// visible: `sim-vehicle` is the same model and has no settings, so it gets no panel.
  public static IEnumerable<DeviceSettings> MasterSettings() =>
  [
    new()
    {
      CameraId = DemoCameraIds.For("sim-master"),
      Kind = DeviceSettingsRules.DigitalInput, Token = "input", Name = "Input",
      ArmMode = DeviceSettingsRules.Inherit,
    },
    new()
    {
      CameraId = DemoCameraIds.For("sim-master"),
      Kind = DeviceSettingsRules.Relay, Token = "lamp", Name = "Lamp",
      ArmMode = DeviceSettingsRules.Inherit,
    },
    new()
    {
      CameraId = DemoCameraIds.For("sim-master"),
      Kind = DeviceSettingsRules.Relay, Token = "peer", Name = "Peer",
      ArmMode = DeviceSettingsRules.Inherit,
    },
    new()
    {
      CameraId = DemoCameraIds.For("sim-master"),
      Kind = DeviceSettingsRules.Relay, Token = "loop", Name = "Loop",
      ArmMode = DeviceSettingsRules.Inherit,
    },
  ];

  public async Task<Plan> InstallAsync(IReadOnlyCollection<string> presentCameraIds, CancellationToken ct)
  {
    var configured = new List<string>();
    var skipped = new List<string>();

    var wanted = MasterSettings().Append(SensorSettings()).ToList();
    foreach (var settings in wanted)
    {
      var name = $"{CameraNameOf(settings.CameraId)}/{settings.Token}";

      // A camera the deployment does not have is reported, not an error: the world is valid
      // without it, and installing again once the camera exists configures the part.
      if (!presentCameraIds.Contains(settings.CameraId))
      {
        skipped.Add(name);
        continue;
      }

      await devices.SetAsync(settings, ct);
      configured.Add(name);
    }

    // The same signal the gRPC path sends: without it VmsMap keeps the old arming for its TTL,
    // and the demo's first contact would not raise anything. One per camera, after all of that
    // camera's parts are written.
    foreach (var cameraId in wanted.Select(s => s.CameraId).Distinct(StringComparer.Ordinal))
    {
      if (presentCameraIds.Contains(cameraId))
        await notifier.NotifyAsync(cameraId, ct);
    }

    return new Plan(configured, skipped);
  }

  private static string CameraNameOf(string cameraId) =>
    cameraId == DemoCameraIds.For("sim-master") ? "sim-master" : "sim-ptz";
}
