using System;
using System.Threading;
using System.Threading.Tasks;

namespace VmsMap.Clients
{
  /// <summary>
  /// Where a camera says it stands, in WGS84, and the bearing it says it looks along — a compass
  /// bearing (0 = north, clockwise), or null for a camera that says nothing about its orientation.
  /// </summary>
  public readonly record struct CameraGeoPoint(double Lat, double Lon, double? HeadingDeg = null)
  {
    private const double EarthRadiusMeters = 6_378_137;

    /// <summary>
    /// How far two bearings are apart, in degrees, the short way round; zero when either of them
    /// reported none, because a camera that says nothing about its bearing has not turned.
    /// </summary>
    public double HeadingDeltaDegTo(CameraGeoPoint other)
    {
      if (HeadingDeg is not { } mine || other.HeadingDeg is not { } theirs) return 0;
      var delta = Math.Abs(mine - theirs) % 360;
      return Math.Min(delta, 360 - delta);
    }

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
