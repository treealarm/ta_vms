using System.Threading;
using System.Threading.Tasks;
using Vms.Cfg;

namespace VmsMap.Clients
{
  internal sealed class VmsCfgCameraStateSource : ICameraStateSource
  {
    private readonly CfgService.CfgServiceClient _cfg;

    public VmsCfgCameraStateSource(CfgService.CfgServiceClient cfg)
    {
      _cfg = cfg ?? throw new System.ArgumentNullException(nameof(cfg));
    }

    public async Task<bool> GetArmedAsync(string cameraId, CancellationToken ct)
    {
      var state = await _cfg.GetCamStateAsync(
        new GetCamStateRequest { CameraId = cameraId }, cancellationToken: ct);
      return state.Armed;
    }
  }
}
