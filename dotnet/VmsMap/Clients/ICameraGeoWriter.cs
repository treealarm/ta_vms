using System.Threading;
using System.Threading.Tasks;

namespace VmsMap.Clients
{
  /// <summary>
  /// Moves a camera's canonical node to a place the device itself reported, and — when the device
  /// also reports a bearing — turns its reference placement to it. Nothing else about the placement
  /// changes: not the parent, not the field of view, not the pictures. A camera with no reference
  /// placement has nowhere to move to.
  /// </summary>
  public interface ICameraGeoWriter
  {
    /// <param name="headingDeg">A compass bearing (0 = north, clockwise), or null to leave the heading as it is.</param>
    /// <returns>False when the camera has no reference placement.</returns>
    Task<bool> MoveReferencePointAsync(
      string cameraId, double lat, double lon, double? headingDeg, CancellationToken ct);
  }
}
