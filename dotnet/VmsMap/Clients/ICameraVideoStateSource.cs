using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace VmsMap.Clients
{
  /// <summary>Whether a stream was seen delivering frames — the video half of presence.</summary>
  public enum VideoChannelState
  {
    Unknown,
    Alive,
    NoVideo,
  }

  // The video state of each camera's streams, read from the node's VmsFs. Addressing only: the map
  // asks for the cameras it is about to judge and reads the current answer, instead of trusting a
  // message that may have been overtaken.
  public interface ICameraVideoStateSource
  {
    /// <summary>
    /// Per camera, per stream. A camera or stream absent from the answer has not been heard from,
    /// which is not the same as dead.
    /// </summary>
    Task<IReadOnlyDictionary<string, IReadOnlyDictionary<string, VideoChannelState>>> GetAsync(
      IReadOnlyCollection<CameraInfo> cameras, CancellationToken ct);
  }
}
