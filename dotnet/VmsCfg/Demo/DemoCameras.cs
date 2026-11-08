using Microsoft.Extensions.Logging;
using Vms.Cfg;

namespace VmsCfg.Demo;

/// <summary>
/// One of the demo world's cameras as OnvifSim publishes it: the world's name for it, the name the
/// web shows, its ONVIF port and whether it has a PTZ head.
/// </summary>
public sealed record DemoCameraSetup(
  string Name, string Title, int OnvifPort, bool HasTelemetry, bool GeoFromDevice)
{
  public string CameraId => DemoCameraIds.For(Name);

  /// <summary>
  /// The camera the administrator's own form would write, field for field: the same ONVIF
  /// connection, credentials and single `main` stream that `POST /api/live/cameras` takes.
  /// </summary>
  public CameraSetup ToProto(string host, string serverId)
  {
    var camera = new CameraSetup
    {
      CameraId = CameraId,
      Name = $"OnvifSim {Title}",
      ServerId = serverId,
      OnvifConn = new OnvifConnection
      {
        Ip = host,
        Port = OnvifPort,
        Xaddr = $"http://{host}:{OnvifPort}/onvif/device_service",
      },
      Cred = new Credentials { User = "admin", Pass = "admin" },
      HasTelemetry = HasTelemetry,
      // The one that rides the car: its place on the map is what it reports over ONVIF, not what
      // an administrator placed. Everything else is bolted where the world puts it.
      GeoFromDevice = GeoFromDevice,
    };
    camera.Streams.Add(new CameraStream
    {
      StreamName = "main",
      ProfileToken = DemoCameras.ProfileToken,
      Path = string.Empty,
      WebRtc = true,
    });
    return camera;
  }
}

/// <summary>
/// The five OnvifSim cameras the demo world is built around, installed with it. They stay after the
/// demo is removed, like the video wall's cameras: they belong to the deployment.
/// </summary>
/// <remarks>
/// The addresses are the simulator's: its ONVIF ports run from `ONVIFSIM_ONVIF_BASE_PORT` in the
/// world's own camera order, so this list is that order. `DEMO_ONVIFSIM_HOST` names the machine a
/// service can reach it at — on a single machine that is the host itself, in Docker the gateway.
/// Without it (a dev checkout without the demo profile) the world still installs and the report
/// names the cameras it left for the administrator.
/// </remarks>
public sealed class DemoCameras(CameraConfigService config, ILogger<DemoCameras> log)
{
  public const string HostVariable = "DEMO_ONVIFSIM_HOST";

  // The node these cameras hang off. A camera has to be pinned to a media server, and in a demo
  // installation that is the machine running it — the same variable VmsOnvif is given.
  private const string ServerVariable = "OWN_SERVER_ID";

  // OnvifSim's first ONVIF port; the world's cameras take the following ones in order.
  private const int FirstOnvifPort = 8101;
  public const string ProfileToken = "Profile_2";

  // The world's cameras, in MarylandWorld's order — the ONVIF port is the base plus that index.
  // The titles are the models' names, so the web shows what the simulator calls them. The last
  // column marks the camera on the car, which takes its place from the device.
  private static readonly (string Name, string Title, bool Ptz, bool GeoFromDevice)[] World =
  [
    ("sim-master", "Main room master", false, false),
    ("sim-ptz", "Main room slave (PTZ)", true, false),
    ("sim-master2", "Building master 2", false, false),
    ("sim-ptz2", "Campus slave 2 (PTZ)", true, false),
    ("sim-vehicle", "Vehicle camera", false, true),
  ];

  /// <summary>What an install writes, without touching the database.</summary>
  public static IReadOnlyList<DemoCameraSetup> Plan() =>
  [
    .. World.Select((camera, index) =>
      new DemoCameraSetup(camera.Name, camera.Title, FirstOnvifPort + index, camera.Ptz,
        camera.GeoFromDevice)),
  ];

  /// <summary>The cameras written, and the ones left for the administrator.</summary>
  public sealed record Result(IReadOnlyList<string> Installed, IReadOnlyList<string> Skipped);

  public async Task<Result> InstallAsync(CancellationToken ct)
  {
    var plan = Plan();
    var host = Environment.GetEnvironmentVariable(HostVariable);
    if (string.IsNullOrWhiteSpace(host))
    {
      log.LogInformation(
        "{Variable} is not set: the demo's cameras are left to the administrator, and the install " +
        "reports which ones", HostVariable);
      return new Result([], [.. plan.Select(camera => camera.Name)]);
    }

    // A misconfiguration rather than a missing feature: the cameras cannot be pinned to a node
    // without it, and guessing one would put them on somebody else's machine.
    var serverId = Environment.GetEnvironmentVariable(ServerVariable);
    if (string.IsNullOrWhiteSpace(serverId))
      throw new InvalidOperationException($"Required environment variable '{ServerVariable}' is not set");

    var installed = new List<string>();
    foreach (var camera in plan)
    {
      var status = await config.ConfigureCameraAsync(camera.ToProto(host, serverId), ct);
      if (!status.Success)
        throw new InvalidOperationException(
          $"The demo camera '{camera.Name}' could not be configured: {status.Message}");
      installed.Add(camera.Name);
      log.LogInformation(
        "Demo camera '{Camera}' configured (ONVIF {Host}:{Port}, server {Server})",
        camera.Name, host, camera.OnvifPort, serverId);
    }

    return new Result(installed, []);
  }
}
