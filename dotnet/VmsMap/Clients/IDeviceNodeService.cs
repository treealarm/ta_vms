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
  }
}
