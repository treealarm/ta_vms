using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using Vms.Cfg;

namespace VmsMap.Clients
{
  /// <summary>
  /// The camera's effective flag, which is VmsCfg's own column: the one thing services near the
  /// hardware read. The map is the only writer — the mode that decides the flag lives in the map,
  /// and a digital input's behaviour never leaves it.
  /// </summary>
  public interface ICfgArmingWriter
  {
    /// <summary>
    /// Writes the flag the map resolved and answers whether a row changed. A no-op announces
    /// nothing, which is what ends the round of "map writes, event arrives, map looks again".
    /// </summary>
    Task<bool> SetCamArmedAsync(string cameraId, bool armed, CancellationToken ct);
  }

  internal sealed class VmsCfgArmingWriter : ICfgArmingWriter
  {
    private readonly CfgService.CfgServiceClient _cfg;

    public VmsCfgArmingWriter(CfgService.CfgServiceClient cfg)
    {
      _cfg = cfg ?? throw new System.ArgumentNullException(nameof(cfg));
    }

    // An assignment, not a toggle: writing the same flag twice leaves one flag.
    public async Task<bool> SetCamArmedAsync(string cameraId, bool armed, CancellationToken ct)
    {
      var response = await _cfg.SetCamArmedAsync(
        new SetCamArmedRequest { CameraId = cameraId, Armed = armed },
        cancellationToken: ct);
      return response.Changed;
    }
  }
}
