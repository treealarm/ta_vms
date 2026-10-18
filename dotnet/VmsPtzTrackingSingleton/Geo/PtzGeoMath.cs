namespace VmsPtzTrackingSingleton.Geo;

/// <summary>Direction and distance from a camera to a point.</summary>
/// <param name="AzimuthDeg">Compass bearing, 0 = north, clockwise, in [0, 360).</param>
/// <param name="ElevationDeg">Angle above the horizontal; negative looks down.</param>
public readonly record struct Bearing(double AzimuthDeg, double ElevationDeg, double DistanceM);

/// <summary>One landmark the camera was centred on: where it is from the camera, and where the camera pointed.</summary>
/// <param name="Zoom">The zoom a slave uses for an object at this landmark's distance; null when none was recorded.</param>
public sealed record GeoObservationSample(string LandmarkId, Bearing Bearing, float Pan, float Tilt, float? Zoom = null);

/// <summary>How far the rest of the observations put this one, in the camera's own units.</summary>
public sealed record GeoResidual(float Pan, float Tilt);

/// <summary>
/// pan = PanIntercept + PanSlope * s, tilt = TiltIntercept + TiltSlope * e, where s is the azimuth
/// clockwise from <see cref="SeamAzimuthDeg"/>, in [0, 360), and e the elevation.
/// </summary>
/// <param name="RefAzimuthDeg">
/// The middle of the sector the landmarks cover; the azimuth bounds are relative to it, unwrapped
/// into ±180.
/// </param>
/// <param name="SeamAzimuthDeg">
/// Where the camera's pan is taken to wrap round: the middle of the gap between neighbouring
/// landmarks that its readings jump across.
/// </param>
/// <param name="FoldsPan">
/// The camera counts a whole turn as ONVIF's generic -1..1, so an aim past either end is the same
/// direction a turn back.
/// </param>
/// <param name="Residuals">Leave-one-out, per landmark id; null where the rest cannot be fitted.</param>
public sealed record PtzGeoFit(
  double RefAzimuthDeg,
  double SeamAzimuthDeg,
  double PanIntercept,
  double PanSlope,
  double TiltIntercept,
  double TiltSlope,
  double MinRelAzimuthDeg,
  double MaxRelAzimuthDeg,
  double MinElevationDeg,
  double MaxElevationDeg,
  bool FoldsPan,
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
/// A head that pans round and round counts from a seam of its own, where its readings jump from one
/// end of their range to the other. It is wherever the installer happened to point the camera --
/// inside a sector of 90° one time in four -- so it is found rather than assumed: it lies in one
/// of the gaps between neighbouring landmarks, and in the right gap the pan is one straight line,
/// while in any other the jump of a full turn is a step no line fits. Each gap is tried and the
/// best line decides. Two landmarks fit either way round; with nothing to tell the two apart, the
/// seam is put behind them, in the wider gap. Nothing about the length of a turn in the camera's
/// units is needed, which is what keeps this free of the camera's declared ranges.
/// </para>
/// <para>
/// Within its gap the seam is put halfway, so a target between there and the true seam comes out past
/// the end of the camera's range, which a camera that checks its range refuses. In ONVIF's generic
/// space -1..1 is the whole range by definition: when the slope says a turn there is 2, the head
/// pans round and such an aim is folded back by a turn. A camera counting in other units is left as
/// it is. The sector the landmarks cover, outside which an aim is
/// extrapolated, is the circle less its widest gap, so landmarks either side of north are one
/// sector rather than two ends of 0..360.
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

  /// A whole turn in ONVIF's generic space, -1..1.
  private const double GenericTurn = 2;

  /// How far a fitted turn may be from <see cref="GenericTurn"/> and still be one: slack for the slope's
  /// own error, which a head with a dead zone of a few degrees also falls within, harmlessly.
  private const double TurnTolerance = 0.02;

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
    if (!TryFitLines(observations, null, out var lines, out error))
      return false;

    var residuals = new Dictionary<string, GeoResidual?>(StringComparer.Ordinal);
    foreach (var held in observations)
    {
      // The seam is the camera's, found from every landmark: it stays where it is when one is left
      // out, as the two that may be left could fit either way round.
      var rest = observations.Where(o => !ReferenceEquals(o, held)).ToList();
      if (rest.Count < MinObservations || !TryFitLines(rest, lines.SeamAzimuthDeg, out var other, out _))
      {
        residuals[held.LandmarkId] = null;
        continue;
      }

      var (pan, tilt) = other.Evaluate(held.Bearing);
      residuals[held.LandmarkId] = new GeoResidual((float)Math.Abs(pan - held.Pan), (float)Math.Abs(tilt - held.Tilt));
    }

    fit = new PtzGeoFit(
      lines.RefAzimuthDeg, lines.SeamAzimuthDeg, lines.PanIntercept, lines.PanSlope, lines.TiltIntercept, lines.TiltSlope,
      lines.MinRelAz, lines.MaxRelAz, lines.MinEl, lines.MaxEl,
      FoldsPan(lines.PanSlope, observations), residuals);
    return true;
  }

  public static GeoAim Project(PtzGeoFit fit, Bearing bearing)
  {
    var relAz = Wrap180(bearing.AzimuthDeg - fit.RefAzimuthDeg);
    var pan = fit.PanIntercept + fit.PanSlope * FromSeam(bearing.AzimuthDeg, fit.SeamAzimuthDeg);
    if (fit.FoldsPan)
      pan -= GenericTurn * Math.Floor((pan + 1) / GenericTurn);
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

  /// <summary>Whether the camera reads -1..1 and the slope makes a full turn of that.</summary>
  private static bool FoldsPan(double panSlope, IReadOnlyList<GeoObservationSample> obs) =>
    Math.Abs(360 * Math.Abs(panSlope) - GenericTurn) <= TurnTolerance * GenericTurn
    && obs.All(o => o.Pan is >= -1 and <= 1);

  /// <summary>An azimuth measured clockwise from the seam, in [0, 360).</summary>
  private static double FromSeam(double azimuthDeg, double seamDeg)
  {
    var d = (azimuthDeg - seamDeg) % 360;
    return d < 0 ? d + 360 : d;
  }

  private sealed record Lines(
    double RefAzimuthDeg, double SeamAzimuthDeg, double PanIntercept, double PanSlope, double TiltIntercept,
    double TiltSlope, double MinRelAz, double MaxRelAz, double MinEl, double MaxEl)
  {
    public (double Pan, double Tilt) Evaluate(Bearing b) =>
      (PanIntercept + PanSlope * FromSeam(b.AzimuthDeg, SeamAzimuthDeg), TiltIntercept + TiltSlope * b.ElevationDeg);
  }

  /// <summary>An empty stretch of the circle between two neighbouring landmarks.</summary>
  private readonly record struct Gap(double MiddleDeg, double WidthDeg);

  /// <param name="seamDeg">Where the camera's pan wraps; null finds it from these observations.</param>
  private static bool TryFitLines(
    IReadOnlyList<GeoObservationSample> obs, double? seamDeg, out Lines lines, out string? error)
  {
    lines = null!;
    if (obs.Count < MinObservations)
    {
      error = $"Centre the camera on at least {MinObservations} landmarks";
      return false;
    }

    var gaps = Gaps(obs.Select(o => o.Bearing.AzimuthDeg));
    var refAz = Wrap180(gaps.MaxBy(g => g.WidthDeg).MiddleDeg + 180);
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

    var seam = seamDeg ?? FindSeam(obs, gaps);
    var (panA, panB) = LeastSquares(obs.Select(o => FromSeam(o.Bearing.AzimuthDeg, seam)).ToArray(), pan);
    var (tiltA, tiltB) = LeastSquares(el, tilt);
    lines = new Lines(refAz, seam, panA, panB, tiltA, tiltB, az.Min(), az.Max(), el.Min(), el.Max());
    error = null;
    return true;
  }

  /// <summary>The gaps between neighbouring azimuths round the circle, the one across north included.</summary>
  private static List<Gap> Gaps(IEnumerable<double> azimuthsDeg)
  {
    var sorted = azimuthsDeg.Select(a => FromSeam(a, 0)).Order().ToList();
    var gaps = new List<Gap>(sorted.Count);
    for (var i = 0; i < sorted.Count; i++)
    {
      var from = sorted[i];
      var width = i + 1 < sorted.Count ? sorted[i + 1] - from : sorted[0] + 360 - from;
      gaps.Add(new Gap(FromSeam(from + width / 2, 0), width));
    }
    return gaps;
  }

  /// <summary>The middle of the gap in which the pan readings make the straightest line.</summary>
  /// <remarks>Tried widest first, so a tie goes to the gap behind the landmarks.</remarks>
  private static double FindSeam(IReadOnlyList<GeoObservationSample> obs, IReadOnlyList<Gap> gaps)
  {
    var widestFirst = gaps.OrderByDescending(g => g.WidthDeg).ToList();
    if (obs.Count == MinObservations)
      return widestFirst[0].MiddleDeg;

    var pan = obs.Select(o => (double)o.Pan).ToArray();
    var best = widestFirst[0].MiddleDeg;
    var bestMiss = double.MaxValue;
    foreach (var gap in widestFirst)
    {
      var x = obs.Select(o => FromSeam(o.Bearing.AzimuthDeg, gap.MiddleDeg)).ToArray();
      var (a, b) = LeastSquares(x, pan);
      var miss = x.Select((xi, i) => Math.Pow(a + b * xi - pan[i], 2)).Sum();
      if (miss < bestMiss)
        (best, bestMiss) = (gap.MiddleDeg, miss);
    }
    return best;
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
}
