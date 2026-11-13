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
    private const string Inherit = "inherit";

    private readonly CfgService.CfgServiceClient _cfg;

    public VmsCfgCameraStateSource(CfgService.CfgServiceClient cfg)
    {
      _cfg = cfg ?? throw new ArgumentNullException(nameof(cfg));
    }

    public async Task<CameraArmState?> GetStateAsync(string cameraId, CancellationToken ct)
    {
      // The read answers the default for a camera that never had a row, so its absence is told by
      // the mode it answers with: a camera that exists and inherits is also off, and either way
      // the caller's next step is to ask the zone.
      var state = await _cfg.GetCamStateAsync(
        new GetCamStateRequest { CameraId = cameraId }, cancellationToken: ct);
      return new CameraArmState(cameraId, state.Armed, ModeOf(state.ArmMode));
    }

    public async Task<IReadOnlyDictionary<string, CameraArmState>> GetStatesAsync(
      IReadOnlyCollection<string> cameraIds, CancellationToken ct)
    {
      var result = new Dictionary<string, CameraArmState>(StringComparer.Ordinal);
      if (cameraIds.Count == 0)
        return result;

      var request = new GetCamStatesRequest();
      request.CameraIds.AddRange(cameraIds);
      var response = await _cfg.GetCamStatesAsync(request, cancellationToken: ct);

      foreach (var state in response.States)
        result[state.CameraId] = new CameraArmState(state.CameraId, state.Armed, ModeOf(state.ArmMode));

      // VmsCfg answers only the rows it has; a camera without one follows its zone and is off.
      foreach (var cameraId in cameraIds)
        result.TryAdd(cameraId, new CameraArmState(cameraId, false, Inherit));

      return result;
    }

    // Protobuf omits a default-valued field, and the default of the mode is the word itself.
    private static string ModeOf(string mode) => string.IsNullOrEmpty(mode) ? Inherit : mode;
  }
}
