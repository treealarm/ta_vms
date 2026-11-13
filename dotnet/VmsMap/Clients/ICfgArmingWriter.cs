using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using Vms.Cfg;

namespace VmsMap.Clients
{
  /// <summary>
  /// The two arming columns VmsCfg owns: a digital input's arm_mode and a camera's flag. The map
  /// delegates here rather than keeping a second copy of them — the device card reads the same
  /// rows, and a promotion of some other placement must not start a race with this one.
  /// </summary>
  public interface ICfgArmingWriter
  {
    Task SetPartArmModeAsync(string cameraId, string token, string mode, CancellationToken ct);

    Task SetCameraArmedAsync(string cameraId, bool armed, CancellationToken ct);

    /// <summary>
    /// The flag the map derives for a camera that inherits its zone. It lands only while the camera
    /// still inherits, and answers whether anything changed — a no-op announces nothing, which is
    /// what ends the round of "map writes, event arrives, map looks again".
    /// </summary>
    Task<bool> SetCameraEffectiveArmedAsync(string cameraId, bool armed, CancellationToken ct);
  }

  internal sealed class VmsCfgArmingWriter : ICfgArmingWriter
  {
    private readonly CfgService.CfgServiceClient _cfg;

    public VmsCfgArmingWriter(CfgService.CfgServiceClient cfg)
    {
      _cfg = cfg ?? throw new System.ArgumentNullException(nameof(cfg));
    }

    // A refusal (an unknown token, a relay, an unconfigured input) travels as the RpcException
    // VmsCfg raised: the operator sees "not found" or "failed precondition", not a generic error.
    public async Task SetPartArmModeAsync(
      string cameraId, string token, string mode, CancellationToken ct)
    {
      await _cfg.SetDeviceArmModeAsync(
        new SetDeviceArmModeRequest { CameraId = cameraId, Token = token, ArmMode = mode },
        cancellationToken: ct);
    }

    // An assignment, not a toggle: writing the same flag twice leaves one flag.
    public async Task SetCameraArmedAsync(string cameraId, bool armed, CancellationToken ct)
    {
      await _cfg.SetCamStateAsync(
        new SetCamStateRequest { CameraId = cameraId, Armed = armed },
        cancellationToken: ct);
    }

    public async Task<bool> SetCameraEffectiveArmedAsync(
      string cameraId, bool armed, CancellationToken ct)
    {
      var response = await _cfg.SetCamEffectiveArmingAsync(
        new SetCamEffectiveArmingRequest { CameraId = cameraId, Armed = armed },
        cancellationToken: ct);
      return response.Changed;
    }
  }
}
