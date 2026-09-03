using System.Collections.Concurrent;
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
  private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(1);

  private sealed record Entry(PtzCalibrationModel? Model, DateTime FetchedAt);

  private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

  public void Invalidate(IReadOnlyCollection<string> cameraIds)
  {
    if (cameraIds.Count == 0)
    {
      _entries.Clear();
      return;
    }

    foreach (var cameraId in cameraIds)
      _entries.TryRemove(cameraId, out _);
  }

  public async Task<PtzCalibrationModel?> GetAsync(string cameraId, CancellationToken ct)
  {
    if (_entries.TryGetValue(cameraId, out var cached) && DateTime.UtcNow - cached.FetchedAt < Ttl)
      return cached.Model;

    try
    {
      var response = await cfg.GetPtzCalibrationAsync(
        new GetPtzCalibrationRequest { CameraId = cameraId }, cancellationToken: ct);
      PtzCalibrationModel? model = null;
      if (response.Found)
      {
        var c = response.Calibration;
        model = new PtzCalibrationModel(c.PanDx, c.PanDy, c.TiltDx, c.TiltDy);
      }

      _entries[cameraId] = new Entry(model, DateTime.UtcNow);
      return model;
    }
    catch (Exception ex)
    {
      logger.LogWarning(ex, "Could not load PTZ calibration for camera '{Camera}'", cameraId);
      return cached?.Model;
    }
  }
}
