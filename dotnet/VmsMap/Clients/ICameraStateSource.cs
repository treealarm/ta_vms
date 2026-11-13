using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace VmsMap.Clients
{
  /// <summary>
  /// A camera's arming as VmsCfg keeps it: the effective flag every other service reads, and the
  /// mode it was decided with. `inherit` means the flag belongs to the map, which derives it from
  /// the zone the camera stands in.
  /// </summary>
  public sealed record CameraArmState(string CameraId, bool Armed, string ArmMode);

  // The armed flag VmsCfg keeps for a camera. It is a property of the camera, not a map state:
  // the operator's tree shows it beside the camera, while ALARM and CONNECTED are map states.
  public interface ICameraStateSource
  {
    /// <summary>The camera's state, or null when it never had one — which means it inherits.</summary>
    Task<CameraArmState?> GetStateAsync(string cameraId, CancellationToken ct);

    /// <summary>
    /// The same state for a page's worth of cameras. A camera with no row answers with the default
    /// — off, and following its zone — so a caller never has to ask whether an id is missing.
    /// </summary>
    Task<IReadOnlyDictionary<string, CameraArmState>> GetStatesAsync(
      IReadOnlyCollection<string> cameraIds, CancellationToken ct);
  }
}
