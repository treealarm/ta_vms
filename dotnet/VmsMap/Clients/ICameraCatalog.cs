using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace VmsMap.Clients
{
  // What the map needs to know about a camera: that it exists, how to name its tree node, which
  // node it belongs to, whether its place is the device's to report, and what its presence can be
  // judged from. Cameras live in VmsCfg's database, so every answer is a cross-service read — the
  // map never keeps a second camera table.
  //
  // VideoStreams lists the non-disabled stream names: "all configured streams are dead" and "one
  // stream is dead while another was never heard" are different answers, and only the names tell
  // them apart. A camera with no streams has none, and its only channel is ONVIF.
  public sealed record CameraInfo(
    string CameraId,
    string Name,
    string ServerId,
    bool GeoFromDevice = false,
    bool Disabled = false,
    IReadOnlyList<string>? VideoStreams = null,
    bool HasOnvif = false)
  {
    /// <summary>False only when the camera is known to have no configured streams at all.</summary>
    public bool HasConfiguredStreams => VideoStreams is not { Count: 0 };
  }

  public interface ICameraCatalog
  {
    /// <summary>Null when VmsCfg does not know the camera.</summary>
    Task<CameraInfo?> GetAsync(string cameraId, CancellationToken ct);

    /// <summary>Every camera, or the ones of one node when <paramref name="serverId"/> is set.</summary>
    Task<List<CameraInfo>> ListAsync(string? serverId, CancellationToken ct);
  }
}
