namespace VmsPtzTrackingSingleton.Geo;

/// <summary>One landmark marked in a fixed camera's picture: where its foot is in the frame, and on the ground.</summary>
/// <param name="X">Across the frame, 0..1 from the left.</param>
/// <param name="Y">Down the frame, 0..1 from the top.</param>
/// <param name="FloorElevationM">The elevation of the floor the landmark stands on.</param>
public sealed record GroundSample(string LandmarkId, double X, double Y, Enu Ground, double FloorElevationM);

/// <summary>
/// Maps a fixed camera's picture onto the ground it looks at: a homography from the normalised
/// frame (0..1, as detection boxes are given) to metres in a local frame.
/// </summary>
/// <remarks>
/// <para>
/// A camera that does not move sees a flat floor through a projective transform, so four points
/// whose place both in the picture and on the ground is known fix it exactly, whatever the mount,
/// tilt and focal length. No camera model is involved and nothing about the camera's own position
/// is assumed -- which is also why it says nothing about anything above that floor.
/// </para>
/// <para>
/// What the transform does not capture is lens distortion: a wide lens bends straight lines towards
/// the edges of the frame, and points there land off by more. The leave-one-out residuals show it,
/// and landmarks spread over the part of the picture that matters keep it small where it counts.
/// </para>
/// <para>
/// Fitted by the direct linear transform with h33 = 1, on coordinates normalised as Hartley
/// recommends (centroid at zero, mean distance √2) so the system is well conditioned whatever the
/// units. h33 = 0 would need the centroid of the marks to lie on the horizon, which marks on the
/// ground cannot.
/// </para>
/// </remarks>
public sealed class GroundHomography
{
  /// <summary>Four points fix a homography exactly; the fifth is what lets them be checked.</summary>
  public const int MinSamples = 4;

  /// Points closer than this to a common line, relative to their spread, fix no plane.
  private const double MinSpreadRatio = 0.05;

  /// Below this the normal equations are singular: three of the points share a line.
  private const double MinPivot = 1e-10;

  /// Floors closer than this are the same floor.
  private const double FloorToleranceM = 0.05;

  // Row-major 3×3, normalised so that every marked point has a positive denominator: a point with a
  // denominator of the other sign is beyond the horizon.
  private readonly double[] _h;

  private GroundHomography(double[] h, double floorElevationM, IReadOnlyDictionary<string, double?> residuals)
  {
    _h = h;
    FloorElevationM = floorElevationM;
    Residuals = residuals;
  }

  /// <summary>
  /// The elevation of the floor the picture is mapped onto -- that of every mark, which the fit
  /// refuses to mix -- so a point projected onto it has a height as well as a place.
  /// </summary>
  public double FloorElevationM { get; }

  /// <summary>
  /// How far the fit puts each landmark from where it stands, in metres, per landmark id; null for
  /// all of them when there are only four marks, which the fit passes through exactly.
  /// </summary>
  /// <remarks>
  /// Measured against the fit of all the marks, as georeferencing tools do, not leave-one-out as the
  /// PTZ fit is. A homography has eight degrees of freedom, so with a handful of marks leaving out a
  /// corner one makes the rest extrapolate: a single misplaced landmark then showed as a 50 m miss
  /// on a correct corner and 3 m on itself. Fitted with all of them, the wrong one stands out.
  /// </remarks>
  public IReadOnlyDictionary<string, double?> Residuals { get; }

  /// <returns>False with a reason the administrator can act on when the marks cannot carry a fit.</returns>
  public static bool TryFit(IReadOnlyList<GroundSample> samples, out GroundHomography? fit, out string? error)
  {
    fit = null;
    if (!TrySolve(samples, out var h, out error))
      return false;

    var residuals = new Dictionary<string, double?>(StringComparer.Ordinal);
    foreach (var sample in samples)
    {
      // Every mark has a positive denominator by construction, so it always projects.
      var at = Apply(h, sample.X, sample.Y)!.Value;
      residuals[sample.LandmarkId] = samples.Count > MinSamples
        ? Math.Sqrt(Square(at.East - sample.Ground.East) + Square(at.North - sample.Ground.North))
        : null;
    }

    fit = new GroundHomography(h, samples[0].FloorElevationM, residuals);
    return true;
  }

  /// <returns>Where on the ground a point of the picture is; null beyond the horizon.</returns>
  public Enu? Project(double x, double y) => Apply(_h, x, y);

  /// <summary>
  /// The same transform with WGS84 as its output: (x, y, 1) maps to (lat·w, lon·w, w). Exact,
  /// because the frame's conversion is linear, and what a browser needs to project detections
  /// without any of this code. A point is beyond the horizon when w ≤ 0.
  /// </summary>
  public double[] ToWgs84(EnuFrame frame)
  {
    var (lat0, lon0) = frame.FromEnu(new Enu(0, 0));
    var (latE, lonE) = frame.FromEnu(new Enu(1, 0));
    var (latN, lonN) = frame.FromEnu(new Enu(0, 1));
    double[] toWgs84 =
    [
      latE - lat0, latN - lat0, lat0,
      lonE - lon0, lonN - lon0, lon0,
      0, 0, 1,
    ];
    return Multiply(toWgs84, _h);
  }

  private static bool TrySolve(IReadOnlyList<GroundSample> samples, out double[] h, out string? error)
  {
    h = [];
    if (samples.Count < MinSamples)
    {
      error = $"Mark at least {MinSamples} landmarks; {samples.Count} so far";
      return false;
    }

    var floor = samples[0].FloorElevationM;
    if (samples.Any(s => Math.Abs(s.FloorElevationM - floor) > FloorToleranceM))
    {
      error = "The landmarks are on different floors; the picture can be mapped onto one floor only";
      return false;
    }

    var image = samples.Select(s => (s.X, s.Y)).ToList();
    var ground = samples.Select(s => (s.Ground.East, s.Ground.North)).ToList();
    if (!SpreadOut(ground) || !SpreadOut(image))
    {
      error = "The landmarks lie almost on one line; add some away from it";
      return false;
    }

    var tImage = Normalising(image);
    var tGround = Normalising(ground);

    // Two rows per point: X·(h7 x + h8 y + 1) = h1 x + h2 y + h3, and the same for Y with h4..h6.
    var ata = new double[8, 8];
    var atb = new double[8];
    for (var i = 0; i < samples.Count; i++)
    {
      var (x, y) = Apply2(tImage, image[i]);
      var (gx, gy) = Apply2(tGround, ground[i]);
      Accumulate(ata, atb, [x, y, 1, 0, 0, 0, -x * gx, -y * gx], gx);
      Accumulate(ata, atb, [0, 0, 0, x, y, 1, -x * gy, -y * gy], gy);
    }
    if (Solve(ata, atb) is not { } p)
    {
      error = "Three of the landmarks lie on one line; move one of them";
      return false;
    }

    double[] normalised = [p[0], p[1], p[2], p[3], p[4], p[5], p[6], p[7], 1];
    var m = Multiply(Inverse(tGround), Multiply(normalised, tImage));

    // Every mark is on the ground in front of the camera, so all of them must agree on the sign of
    // the denominator; the transform is scaled so that sign is positive.
    var signs = image.Select(pt => Math.Sign(m[6] * pt.X + m[7] * pt.Y + m[8])).Distinct().ToList();
    if (signs.Count != 1 || signs[0] == 0)
    {
      error = "The marks do not fit one floor seen by one camera; check which landmark each one belongs to";
      return false;
    }
    h = signs[0] < 0 ? m.Select(v => -v).ToArray() : m;

    error = null;
    return true;
  }

  private static Enu? Apply(double[] h, double x, double y)
  {
    var w = h[6] * x + h[7] * y + h[8];
    if (w <= 0)
      return null;
    return new Enu((h[0] * x + h[1] * y + h[2]) / w, (h[3] * x + h[4] * y + h[5]) / w);
  }

  /// Whether the points span a plane rather than a line: the smaller principal spread of the
  /// points against the larger one.
  private static bool SpreadOut(List<(double X, double Y)> points)
  {
    var mx = points.Average(p => p.X);
    var my = points.Average(p => p.Y);
    double sxx = 0, syy = 0, sxy = 0;
    foreach (var (x, y) in points)
    {
      sxx += Square(x - mx);
      syy += Square(y - my);
      sxy += (x - mx) * (y - my);
    }
    var mean = (sxx + syy) / 2;
    var root = Math.Sqrt(Square((sxx - syy) / 2) + sxy * sxy);
    var major = mean + root;
    var minor = Math.Max(0, mean - root);
    return major > 0 && Math.Sqrt(minor / major) >= MinSpreadRatio;
  }

  /// Hartley normalisation: moves the centroid to zero and scales the mean distance from it to √2.
  private static double[] Normalising(List<(double X, double Y)> points)
  {
    var mx = points.Average(p => p.X);
    var my = points.Average(p => p.Y);
    var meanDistance = points.Average(p => Math.Sqrt(Square(p.X - mx) + Square(p.Y - my)));
    var s = Math.Sqrt(2) / meanDistance;
    return [s, 0, -s * mx, 0, s, -s * my, 0, 0, 1];
  }

  private static (double X, double Y) Apply2(double[] t, (double X, double Y) p) =>
    (t[0] * p.X + t[1] * p.Y + t[2], t[3] * p.X + t[4] * p.Y + t[5]);

  private static void Accumulate(double[,] ata, double[] atb, double[] row, double rhs)
  {
    for (var i = 0; i < 8; i++)
    {
      atb[i] += row[i] * rhs;
      for (var j = 0; j < 8; j++)
        ata[i, j] += row[i] * row[j];
    }
  }

  /// Gaussian elimination with partial pivoting; null when the system is singular.
  private static double[]? Solve(double[,] a, double[] b)
  {
    const int n = 8;
    for (var col = 0; col < n; col++)
    {
      var pivot = col;
      for (var r = col + 1; r < n; r++)
        if (Math.Abs(a[r, col]) > Math.Abs(a[pivot, col]))
          pivot = r;
      if (Math.Abs(a[pivot, col]) < MinPivot)
        return null;
      if (pivot != col)
      {
        for (var c = 0; c < n; c++)
          (a[col, c], a[pivot, c]) = (a[pivot, c], a[col, c]);
        (b[col], b[pivot]) = (b[pivot], b[col]);
      }
      for (var r = col + 1; r < n; r++)
      {
        var f = a[r, col] / a[col, col];
        for (var c = col; c < n; c++)
          a[r, c] -= f * a[col, c];
        b[r] -= f * b[col];
      }
    }

    var x = new double[n];
    for (var r = n - 1; r >= 0; r--)
    {
      var sum = b[r];
      for (var c = r + 1; c < n; c++)
        sum -= a[r, c] * x[c];
      x[r] = sum / a[r, r];
    }
    return x;
  }

  private static double[] Multiply(double[] a, double[] b)
  {
    var m = new double[9];
    for (var r = 0; r < 3; r++)
      for (var c = 0; c < 3; c++)
        m[r * 3 + c] = a[r * 3] * b[c] + a[r * 3 + 1] * b[3 + c] + a[r * 3 + 2] * b[6 + c];
    return m;
  }

  /// Inverse of a normalising transform: a scale and a shift.
  private static double[] Inverse(double[] t) =>
    [1 / t[0], 0, -t[2] / t[0], 0, 1 / t[4], -t[5] / t[4], 0, 0, 1];

  private static double Square(double v) => v * v;
}
