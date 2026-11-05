using System.Threading;
using System.Threading.Tasks;

namespace VmsMap.Clients
{
  /// <summary>Where a camera says it stands, in WGS84.</summary>
  public readonly record struct CameraGeoPoint(double Lat, double Lon);

  /// <summary>
  /// The device's own answer to "where are you", for the cameras whose place on the map is the
  /// device's to report. Null when the camera reports no location, or cannot be asked at all.
  /// </summary>
  public interface ICameraGeoSource
  {
    Task<CameraGeoPoint?> GetAsync(CameraInfo camera, CancellationToken ct);
  }
}
