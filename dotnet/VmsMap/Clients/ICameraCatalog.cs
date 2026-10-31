using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace VmsMap.Clients
{
  // What the map needs to know about a camera: that it exists, how to name its tree node, and
  // which node it belongs to. Cameras live in VmsCfg's database, so every answer is a cross-service
  // read — the map never keeps a second camera table.
  public sealed record CameraInfo(string CameraId, string Name, string ServerId);

  public interface ICameraCatalog
  {
    /// <summary>Null when VmsCfg does not know the camera.</summary>
    Task<CameraInfo?> GetAsync(string cameraId, CancellationToken ct);

    /// <summary>Every camera, or the ones of one node when <paramref name="serverId"/> is set.</summary>
    Task<List<CameraInfo>> ListAsync(string? serverId, CancellationToken ct);
  }
}
