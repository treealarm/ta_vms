using System.Threading;
using System.Threading.Tasks;

namespace VmsMap.Clients
{
  /// <summary>
  /// Reads where a camera's reference placement says it stands, and moves its canonical node to a
  /// place the device itself reported — turning the placement to the bearing too, when the device
  /// reports one. Nothing else about the placement changes: not the parent, not the field of view,
  /// not the pictures. A camera with no reference placement has nowhere to move to.
  /// </summary>
  public interface ICameraGeoWriter
  {
    /// <summary>
    /// The point the map holds for a camera, with the bearing the placement was last written with;
    /// null when the camera has no reference placement, or the placement has no point on a canvas.
    /// Reading it is how a caller tells an answer that is news from one that is the row again.
    /// </summary>
    Task<CameraGeoPoint?> GetReferencePointAsync(string cameraId, CancellationToken ct);

    /// <param name="headingDeg">A compass bearing (0 = north, clockwise), or null to leave the heading as it is.</param>
    /// <returns>False when the camera has no reference placement.</returns>
    Task<bool> MoveReferencePointAsync(
      string cameraId, double lat, double lon, double? headingDeg, CancellationToken ct);
  }
}
