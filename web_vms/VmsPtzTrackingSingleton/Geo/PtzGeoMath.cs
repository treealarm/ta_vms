namespace VmsPtzTrackingSingleton.Geo;

/// <summary>Direction and distance from a camera to a point.</summary>
/// <param name="AzimuthDeg">Compass bearing, 0 = north, clockwise, in [0, 360).</param>
/// <param name="ElevationDeg">Angle above the horizontal; negative looks down.</param>
public readonly record struct Bearing(double AzimuthDeg, double ElevationDeg, double DistanceM);

/// <summary>One landmark the camera was centred on: where it is from the camera, and where the camera pointed.</summary>
public sealed record GeoObservationSample(string LandmarkId, Bearing Bearing, float Pan, float Tilt);

/// <summary>How far the rest of the observations put this one, in the camera's own units.</summary>
public sealed record GeoResidual(float Pan, float Tilt);

/// <summary>
/// pan = PanIntercept + PanSlope * a, tilt = TiltIntercept + TiltSlope * e, where a is the azimuth
/// relative to <see cref="RefAzimuthDeg"/> (unwrapped into ±180) and e the elevation.
/// </summary>
/// <param name="Residuals">Leave-one-out, per landmark id; null where the rest cannot be fitted.</param>
public sealed record PtzGeoFit(
  double RefAzimuthDeg,
  double PanIntercept,
  double PanSlope,
  double TiltIntercept,
  double TiltSlope,
  double MinRelAzimuthDeg,
  double MaxRelAzimuthDeg,
  double MinElevationDeg,
  double MaxElevationDeg,
  IReadOnlyDictionary<string, GeoResidual?> Residuals);

/// <summary>Where to point, and whether the direction is outside what the landmarks cover.</summary>
public sealed record GeoAim(float Pan, float Tilt, bool Extrapolated);

/// <summary>
/// Calibrates a PTZ camera against landmarks: from where it pointed when centred on each, learns
/// how its pan and tilt follow compass direction and elevation.
/// </summary>
/// <remarks>
/// <para>
/// Two straight lines, fitted independently: pan against azimuth and tilt against elevation. That
/// is what a level-mounted PTZ does -- its pan axis is vertical, so pan is the bearing plus an
/// offset, and tilt is the elevation plus an offset -- and the slopes absorb whatever units the
/// camera reports in (normalised -1..1, degrees) and their sign (an inverted mount). Nothing about
/// the camera's space is assumed: as with the slave binding, the fit is made to its own readings.
/// </para>
/// <para>
/// What the lines do not capture is a camera hung out of level, whose pan axis leans: its tilt then
/// depends on pan as well. The leave-one-out residuals show that as a pattern of misses across the
/// sector, which is the cue to level the mount -- not something to model away with two points.
/// </para>
/// <para>
/// Azimuths are unwrapped around the observations' circular mean, so a set of landmarks either
/// side of north is one continuous sector rather than two ends of 0..360. The camera's own pan seam
/// (where -1 meets 1) is not known and must not run through the observed sector; landmarks spread
/// across the area the camera will actually be aimed at keep it out.
/// </para>
/// </remarks>
public static class PtzGeoMath
{
  /// <summary>Two points fix a line; the third is what lets one be checked.</summary>
  public const int MinObservations = 2;

  /// Below this the landmarks are effectively in one direction and say nothing about the slope.
  public const double MinAzimuthSpreadDeg = 5;

  /// Elevation varies far less than azimuth across a scene, so the bar is lower — but two
  /// landmarks at the same distance and height still cannot fix the tilt slope.
  public const double MinElevationSpreadDeg = 1;

  /// A reading that does not move this much across the spread above is not a reading.
  private const double MinReadingSpread = 1e-4;

  /// Slack on the extrapolation check, so a point on the edge of the covered sector is not flagged.
  private const double EdgeToleranceDeg = 1;

  private const double RadToDeg = 180.0 / Math.PI;

  public static Bearing BearingTo(Enu from, double fromHeightM, Enu to, double toHeightM)
  {
    var dE = to.East - from.East;
    var dN = to.North - from.North;
    var dH = toHeightM - fromHeightM;
    var horizontal = Math.Sqrt(dE * dE + dN * dN);
    var azimuth = Math.Atan2(dE, dN) * RadToDeg;
    if (azimuth < 0) azimuth += 360;
    return new Bearing(azimuth, Math.Atan2(dH, horizontal) * RadToDeg, Math.Sqrt(horizontal * horizontal + dH * dH));
  }

  /// <returns>False with a reason the operator can act on when the observations cannot carry a fit.</returns>
  public static bool TryFit(IReadOnlyList<GeoObservationSample> observations, out PtzGeoFit? fit, out string? error)
  {
    fit = null;
    if (!TryFitLines(observations, out var lines, out error))
      return false;

    var residuals = new Dictionary<string, GeoResidual?>(StringComparer.Ordinal);
    foreach (var held in observations)
    {
      var rest = observations.Where(o => !ReferenceEquals(o, held)).ToList();
      if (rest.Count < MinObservations || !TryFitLines(rest, out var other, out _))
      {
        residuals[held.LandmarkId] = null;
        continue;
      }

      var (pan, tilt) = other.Evaluate(held.Bearing);
      residuals[held.LandmarkId] = new GeoResidual((float)Math.Abs(pan - held.Pan), (float)Math.Abs(tilt - held.Tilt));
    }

    fit = new PtzGeoFit(
      lines.RefAzimuthDeg, lines.PanIntercept, lines.PanSlope, lines.TiltIntercept, lines.TiltSlope,
      lines.MinRelAz, lines.MaxRelAz, lines.MinEl, lines.MaxEl, residuals);
    return true;
  }

  public static GeoAim Project(PtzGeoFit fit, Bearing bearing)
  {
    var relAz = Wrap180(bearing.AzimuthDeg - fit.RefAzimuthDeg);
    var pan = fit.PanIntercept + fit.PanSlope * relAz;
    var tilt = fit.TiltIntercept + fit.TiltSlope * bearing.ElevationDeg;
    var extrapolated =
      relAz < fit.MinRelAzimuthDeg - EdgeToleranceDeg || relAz > fit.MaxRelAzimuthDeg + EdgeToleranceDeg ||
      bearing.ElevationDeg < fit.MinElevationDeg - EdgeToleranceDeg ||
      bearing.ElevationDeg > fit.MaxElevationDeg + EdgeToleranceDeg;
    return new GeoAim((float)pan, (float)tilt, extrapolated);
  }

  /// <summary>Folds an angle difference into (-180, 180].</summary>
  public static double Wrap180(double degrees)
  {
    var d = degrees % 360;
    if (d <= -180) d += 360;
    else if (d > 180) d -= 360;
    return d;
  }

  private sealed record Lines(
    double RefAzimuthDeg, double PanIntercept, double PanSlope, double TiltIntercept, double TiltSlope,
    double MinRelAz, double MaxRelAz, double MinEl, double MaxEl)
  {
    public (double Pan, double Tilt) Evaluate(Bearing b) =>
      (PanIntercept + PanSlope * Wrap180(b.AzimuthDeg - RefAzimuthDeg), TiltIntercept + TiltSlope * b.ElevationDeg);
  }

  private static bool TryFitLines(IReadOnlyList<GeoObservationSample> obs, out Lines lines, out string? error)
  {
    lines = null!;
    if (obs.Count < MinObservations)
    {
      error = $"Centre the camera on at least {MinObservations} landmarks";
      return false;
    }

    var refAz = CircularMeanDeg(obs.Select(o => o.Bearing.AzimuthDeg));
    var az = obs.Select(o => Wrap180(o.Bearing.AzimuthDeg - refAz)).ToArray();
    var el = obs.Select(o => o.Bearing.ElevationDeg).ToArray();
    var pan = obs.Select(o => (double)o.Pan).ToArray();
    var tilt = obs.Select(o => (double)o.Tilt).ToArray();

    if (az.Max() - az.Min() < MinAzimuthSpreadDeg)
    {
      error = "The landmarks are all in one direction from the camera; add one further to the side";
      return false;
    }
    if (el.Max() - el.Min() < MinElevationSpreadDeg)
    {
      error = "The landmarks are all at the same angle below the camera; add one nearer, farther or at another height";
      return false;
    }
    if (pan.Max() - pan.Min() < MinReadingSpread)
    {
      error = "The camera reports the same pan in every direction; its position readings are probably not real";
      return false;
    }
    if (tilt.Max() - tilt.Min() < MinReadingSpread)
    {
      error = "The camera reports the same tilt at every angle; its position readings are probably not real";
      return false;
    }

    var (panA, panB) = LeastSquares(az, pan);
    var (tiltA, tiltB) = LeastSquares(el, tilt);
    lines = new Lines(refAz, panA, panB, tiltA, tiltB, az.Min(), az.Max(), el.Min(), el.Max());
    error = null;
    return true;
  }

  /// <summary>y = a + b·x by ordinary least squares; x must not be constant.</summary>
  private static (double A, double B) LeastSquares(double[] x, double[] y)
  {
    var mx = x.Average();
    var my = y.Average();
    double sxy = 0, sxx = 0;
    for (var i = 0; i < x.Length; i++)
    {
      sxy += (x[i] - mx) * (y[i] - my);
      sxx += (x[i] - mx) * (x[i] - mx);
    }
    var b = sxy / sxx;
    return (my - b * mx, b);
  }

  private static double CircularMeanDeg(IEnumerable<double> degrees)
  {
    double s = 0, c = 0;
    foreach (var d in degrees)
    {
      s += Math.Sin(d / RadToDeg);
      c += Math.Cos(d / RadToDeg);
    }
    return Math.Atan2(s, c) * RadToDeg;
  }
}
