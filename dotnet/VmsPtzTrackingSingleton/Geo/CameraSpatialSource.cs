using Vms.Map;

namespace VmsPtzTrackingSingleton.Geo;

/// <summary>Reads cameras' reference placements, landmark observations and marks from VmsMap and fits them.</summary>
/// <remarks>
/// Not cached itself. An operator aiming at the map, a calibration wizard checking the point it has
/// just recorded, a map opened to show detections -- each needs the configuration as it is this
/// moment, and none runs often enough for a round trip to matter. A slave aimed through the map
/// does run on every detection, and goes through <see cref="SpatialModelCache"/> instead.
/// </remarks>
public sealed class CameraSpatialSource(MapService.MapServiceClient map)
{
  /// <returns>Null when the camera has no reference placement.</returns>
  public async Task<CameraSpatialModel?> LoadAsync(string cameraId, CancellationToken ct)
  {
    var response = await map.GetCameraSpatialModelAsync(
      new CameraRequest { CameraId = cameraId }, cancellationToken: ct);
    return response.Found ? CameraSpatialModel.Build(response) : null;
  }

  /// <summary>Every camera with ground marks whose reference placement shares geometry with the space.</summary>
  public async Task<IReadOnlyList<CameraSpatialModel>> LoadForSpaceAsync(string spaceId, CancellationToken ct)
  {
    var response = await map.ListCameraSpatialModelsAsync(
      new SpaceRequest { SpaceId = spaceId }, cancellationToken: ct);
    return response.Models.Select(CameraSpatialModel.Build).ToList();
  }

  /// <returns>Null when there is no such space.</returns>
  public async Task<Space?> LoadSpaceAsync(string spaceId, CancellationToken ct)
  {
    var response = await map.GetSpaceAsync(new SpaceRequest { SpaceId = spaceId }, cancellationToken: ct);
    return response.Found ? response.Space : null;
  }
}
