using System.Threading;
using System.Threading.Tasks;

namespace VmsMap.Clients
{
  /// <summary>
  /// What a camera reports about one of its devices. <c>HasDevices</c> says the camera has such a
  /// device at all; <c>Live</c> is the state it last announced, and null means it has not announced
  /// one yet — which is not the same as "off".
  /// </summary>
  public readonly record struct CameraDeviceState(bool HasDevices, bool? Live);

  /// <summary>
  /// The live state of a camera's relays and digital inputs. ONVIF has no getter for either: the
  /// camera announces them as Device/Trigger property events, and VmsOnvif caches what it hears.
  /// </summary>
  public interface ICameraDeviceIoSource
  {
    /// <summary>The camera's first relay, or no device at all.</summary>
    Task<CameraDeviceState> GetRelayAsync(CameraInfo camera, CancellationToken ct);

    /// <summary>The camera's first digital input, or no device at all.</summary>
    Task<CameraDeviceState> GetInputAsync(CameraInfo camera, CancellationToken ct);
  }
}
