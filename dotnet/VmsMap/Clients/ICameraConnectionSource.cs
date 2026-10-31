using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace VmsMap.Clients
{
  // Whether a camera's source is reaching the node. The authoritative record is VmsFs's source
  // liveness: video_srv reports the stream state, VmsFs turns the transitions into outage
  // intervals, and an interval without an end is a source lost right now.
  public interface ICameraConnectionSource
  {
    /// <summary>
    /// True means connected, false means an open outage. A camera absent from the answer could not
    /// be asked about; the caller keeps its last known state.
    /// </summary>
    Task<Dictionary<string, bool>> GetConnectedAsync(
      IReadOnlyCollection<CameraInfo> cameras, CancellationToken ct);
  }
}
