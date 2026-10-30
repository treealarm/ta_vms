using System.Threading;
using System.Threading.Tasks;

namespace VmsMap.Clients
{
  // The armed flag VmsCfg keeps for a camera. It is a property of the camera, not a map state:
  // the operator's tree shows it beside the camera, while ALARM and CONNECTED are map states.
  public interface ICameraStateSource
  {
    Task<bool> GetArmedAsync(string cameraId, CancellationToken ct);
  }
}
