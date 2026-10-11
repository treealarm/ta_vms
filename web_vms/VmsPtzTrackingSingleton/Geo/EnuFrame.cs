namespace VmsPtzTrackingSingleton.Geo;

/// <summary>A point in a local frame: metres east and north of its origin.</summary>
public readonly record struct Enu(double East, double North);

/// <summary>
/// Converts WGS84 to metres around an origin, and back.
/// </summary>
/// <remarks>
/// An equirectangular approximation: north is latitude times the earth's radius, east is longitude
/// times the radius scaled by the cosine of the origin's latitude. A few hundred metres from the
/// origin the error is millimetres; it grows with distance and reaches about a metre at 2 km,
/// which is the reach this is meant for. Anything larger needs a real projection, not a bigger
/// version of this.
/// </remarks>
public sealed class EnuFrame(double originLat, double originLon)
{
  /// WGS84 equatorial radius, the one web maps use.
  public const double EarthRadiusM = 6378137.0;

  private const double DegToRad = Math.PI / 180.0;

  private readonly double _metresPerDegLat = EarthRadiusM * DegToRad;
  private readonly double _metresPerDegLon = EarthRadiusM * DegToRad * Math.Cos(originLat * DegToRad);

  public double OriginLat { get; } = originLat;
  public double OriginLon { get; } = originLon;

  public Enu ToEnu(double lat, double lon) =>
    new((lon - OriginLon) * _metresPerDegLon, (lat - OriginLat) * _metresPerDegLat);

  public (double Lat, double Lon) FromEnu(Enu p) =>
    (OriginLat + p.North / _metresPerDegLat, OriginLon + p.East / _metresPerDegLon);
}
