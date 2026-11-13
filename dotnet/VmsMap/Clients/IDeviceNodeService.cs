using System.Threading;
using System.Threading.Tasks;

namespace VmsMap.Clients
{
  /// <summary>
  /// The camera's canonical node in the tree: one per camera, named after it, and keyed back to the
  /// camera id by `device_node`. The demo needs it to hang a replica on, and asks here rather than
  /// reading the table it would then have to keep in step with.
  /// </summary>
  public interface IDeviceNodeService
  {
    /// <summary>Creates the node if it does not exist, and brings its name and parent up to date.</summary>
    Task EnsureCameraAsync(CameraInfo camera, CancellationToken ct);

    /// <summary>
    /// A part's canonical node under its camera, created if it does not exist. Null when the
    /// source kind is not a camera or the deployment does not have that camera.
    /// </summary>
    Task<string?> EnsureAsync(
      string sourceKind, string sourceId, string partKind, string partToken, CancellationToken ct);
  }
}
