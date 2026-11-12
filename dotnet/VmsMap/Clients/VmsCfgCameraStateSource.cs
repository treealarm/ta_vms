using System;
using System.Collections.Generic;
using System.Linq;
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
      _cfg = cfg ?? throw new ArgumentNullException(nameof(cfg));
    }

    public async Task<bool> GetArmedAsync(string cameraId, CancellationToken ct)
    {
      var state = await _cfg.GetCamStateAsync(
        new GetCamStateRequest { CameraId = cameraId }, cancellationToken: ct);
      return state.Armed;
    }

    public async Task<IReadOnlyDictionary<string, bool>> GetArmedManyAsync(
      IReadOnlyCollection<string> cameraIds, CancellationToken ct)
    {
      if (cameraIds.Count == 0)
        return new Dictionary<string, bool>(StringComparer.Ordinal);

      var request = new GetCamStatesRequest();
      request.CameraIds.AddRange(cameraIds);
      var response = await _cfg.GetCamStatesAsync(request, cancellationToken: ct);

      return response.States.ToDictionary(
        s => s.CameraId, s => s.Armed, StringComparer.Ordinal);
    }
  }
}
