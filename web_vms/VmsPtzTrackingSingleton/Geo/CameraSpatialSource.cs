using Vms.Cfg;

namespace VmsPtzTrackingSingleton.Geo;

/// <summary>Reads a camera's reference placement and landmark observations from VmsCfg and fits them.</summary>
/// <remarks>
/// Not cached. Both callers today -- an operator aiming at the map, and the calibration wizard
/// checking the point it has just recorded -- need the configuration as it is this moment, and
/// neither runs often enough for a round trip to matter. A cache invalidated by
/// <c>space-config-changed</c> belongs here once something aims through this on every detection.
/// </remarks>
public sealed class CameraSpatialSource(CfgService.CfgServiceClient cfg)
{
  /// <returns>Null when the camera has no reference placement.</returns>
  public async Task<CameraSpatialModel?> LoadAsync(string cameraId, CancellationToken ct)
  {
    var response = await cfg.GetCameraSpatialModelAsync(new SourceRequest { SourceId = cameraId }, cancellationToken: ct);
    return response.Found ? CameraSpatialModel.Build(response) : null;
  }

  /// <returns>Null when there is no such space.</returns>
  public async Task<Space?> LoadSpaceAsync(string spaceId, CancellationToken ct)
  {
    var response = await cfg.GetSpaceAsync(new SpaceRequest { SpaceId = spaceId }, cancellationToken: ct);
    return response.Found ? response.Space : null;
  }
}
