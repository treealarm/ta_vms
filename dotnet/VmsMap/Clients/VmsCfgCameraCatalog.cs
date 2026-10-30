using Vms.Cfg;

namespace VmsMap.Clients
{
  // The map's only view of the camera core: a placement is written for a camera that exists, and
  // the camera's node is named after it. Cameras live in VmsCfg's database, so this is a deliberate
  // cross-service check where a foreign key used to be.
  internal sealed class VmsCfgCameraCatalog : ICameraCatalog
  {
    private readonly CfgService.CfgServiceClient _cfg;

    public VmsCfgCameraCatalog(CfgService.CfgServiceClient cfg)
    {
      _cfg = cfg ?? throw new ArgumentNullException(nameof(cfg));
    }

    public async Task<CameraInfo?> GetAsync(string cameraId, CancellationToken ct)
    {
      if (string.IsNullOrWhiteSpace(cameraId))
        return null;

      var response = await _cfg.GetCameraAsync(
        new SourceRequest { SourceId = cameraId }, cancellationToken: ct);
      if (!response.Found || response.Setup is null)
        return null;

      return new CameraInfo(response.Setup.CameraId, response.Setup.Name ?? string.Empty);
    }
  }
}
