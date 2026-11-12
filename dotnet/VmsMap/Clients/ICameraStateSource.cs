using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace VmsMap.Clients
{
  // The armed flag VmsCfg keeps for a camera. It is a property of the camera, not a map state:
  // the operator's tree shows it beside the camera, while ALARM and CONNECTED are map states.
  public interface ICameraStateSource
  {
    Task<bool> GetArmedAsync(string cameraId, CancellationToken ct);

    /// <summary>
    /// The same flags for a page's worth of cameras. A camera with no row is absent from the
    /// answer, which means disarmed — the same reading the single call gives.
    /// </summary>
    Task<IReadOnlyDictionary<string, bool>> GetArmedManyAsync(
      IReadOnlyCollection<string> cameraIds, CancellationToken ct);
  }
}
