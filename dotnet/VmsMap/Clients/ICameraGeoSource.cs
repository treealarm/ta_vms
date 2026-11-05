using System;
using System.Threading;
using System.Threading.Tasks;

namespace VmsMap.Clients
{
  /// <summary>Where a camera says it stands, in WGS84.</summary>
  public readonly record struct CameraGeoPoint(double Lat, double Lon)
  {
    private const double EarthRadiusMeters = 6_378_137;

    /// <summary>
    /// How far apart two points are on the ground, in metres. Equirectangular over the same radius
    /// EnuFrame uses: these are hundred-metre questions, not survey ones, and a geodesic would buy
    /// nothing the caller's threshold can use.
    /// </summary>
    public double DistanceMetersTo(CameraGeoPoint other)
    {
      var meanLatRad = (Lat + other.Lat) * Math.PI / 360;
      var north = (other.Lat - Lat) * Math.PI / 180 * EarthRadiusMeters;
      var east = (other.Lon - Lon) * Math.PI / 180 * EarthRadiusMeters * Math.Cos(meanLatRad);
      return Math.Sqrt(north * north + east * east);
    }
  }

  /// <summary>
  /// The device's own answer to "where are you", for the cameras whose place on the map is the
  /// device's to report. Null when the camera reports no location, or cannot be asked at all.
  /// </summary>
  public interface ICameraGeoSource
  {
    Task<CameraGeoPoint?> GetAsync(CameraInfo camera, CancellationToken ct);
  }
}
