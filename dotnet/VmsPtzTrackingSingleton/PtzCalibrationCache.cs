using Vms.Cfg;

namespace VmsPtzTrackingSingleton;

/// <summary>Per-camera PTZ calibration fetched from VmsCfg.</summary>
public sealed record PtzCalibrationModel(
  float PanDx,
  float PanDy,
  float TiltDx,
  float TiltDy);

/// <summary>
/// Cached PTZ calibration per camera. Invalidated through the same pub/sub path as routes.
/// </summary>
public sealed class PtzCalibrationCache(CfgService.CfgServiceClient cfg, ILogger<PtzCalibrationCache> logger)
{
  private readonly TtlCache<PtzCalibrationModel> _cache = new(
    async (cameraId, ct) =>
    {
      var response = await cfg.GetPtzCalibrationAsync(
        new GetPtzCalibrationRequest { CameraId = cameraId }, cancellationToken: ct);
      if (!response.Found)
        return null;
      var c = response.Calibration;
      return new PtzCalibrationModel(c.PanDx, c.PanDy, c.TiltDx, c.TiltDy);
    },
    TimeSpan.FromMinutes(1), logger, "PTZ calibration");

  public void Invalidate(IReadOnlyCollection<string> cameraIds) => _cache.Invalidate(cameraIds);

  public Task<PtzCalibrationModel?> GetAsync(string cameraId, CancellationToken ct) => _cache.GetAsync(cameraId, ct);
}
