using System.Threading;
using System.Threading.Tasks;

namespace VmsMap.Clients
{
  /// <summary>
  /// Moves a camera's canonical node to a place the device itself reported. Nothing else about the
  /// placement changes — not the parent, not the heading or the field of view, not the pictures —
  /// and a camera with no reference placement has nowhere to move to.
  /// </summary>
  public interface ICameraGeoWriter
  {
    /// <returns>False when the camera has no reference placement.</returns>
    Task<bool> MoveReferencePointAsync(
      string cameraId, double lat, double lon, CancellationToken ct);
  }
}
