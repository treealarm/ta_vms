using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vms.Cfg;

namespace VmsMap.Clients
{
  // The map's view of the camera core. A placement is written for a camera that exists, a camera's
  // node is named after it, and the adapter reconciles the whole set after a node-wide change.
  internal sealed class VmsCfgCameraCatalog : ICameraCatalog
  {
    private readonly CfgService.CfgServiceClient _cfg;

    public VmsCfgCameraCatalog(CfgService.CfgServiceClient cfg)
    {
      _cfg = cfg ?? throw new System.ArgumentNullException(nameof(cfg));
    }

    public async Task<CameraInfo?> GetAsync(string cameraId, CancellationToken ct)
    {
      if (string.IsNullOrWhiteSpace(cameraId))
        return null;

      var response = await _cfg.GetCameraAsync(
        new SourceRequest { SourceId = cameraId }, cancellationToken: ct);
      if (!response.Found || response.Setup is null)
        return null;

      return ToInfo(response.Setup);
    }

    public async Task<List<CameraInfo>> ListAsync(string? serverId, CancellationToken ct)
    {
      var response = await _cfg.ListCamerasAsync(
        new ListCamerasRequest { ServerId = serverId ?? string.Empty }, cancellationToken: ct);

      return response.Cameras
        .Where(camera => !string.IsNullOrWhiteSpace(camera.CameraId))
        .Select(ToInfo)
        .ToList();
    }

    internal static CameraInfo ToInfo(CameraSetup setup) =>
      new(setup.CameraId, setup.Name ?? string.Empty, setup.ServerId ?? string.Empty, setup.GeoFromDevice);
  }
}
