using System.Threading;
using System.Threading.Tasks;

namespace VmsMap.Clients
{
  /// <summary>
  /// Whether a camera answers at all, as distinct from whether it delivers video.
  /// <see cref="NoOnvif"/> is a missing channel, and <see cref="Unknown"/> means the question could
  /// not be put — neither may read as an unreachable camera.
  /// </summary>
  public enum CameraReachability
  {
    Unknown,
    Reachable,
    Unreachable,
    NoOnvif,
  }

  // Asks the camera's own node whether the camera is on the network. Best-effort: the map asks only
  // where a stream is known to be dead, and a failed question changes nothing.
  public interface ICameraReachabilityProbe
  {
    Task<CameraReachability> ProbeAsync(CameraInfo camera, CancellationToken ct);
  }
}
