namespace VmsPtzTrackingSingleton;

/// <summary>One marked correspondence: a spot in the master's frame, and where the slave looked.</summary>
public sealed record BindingPoint(
  float MasterX,
  float MasterY,
  float? Pan,
  float? Tilt,
  string? PresetToken,
  string? PresetName);

/// <summary>A fitted mapping from the master's frame to the slave's position space.</summary>
/// <param name="Residuals">
/// How far the rest of the survey lands from each marked point when that point is left out of the
/// fit, in the camera's own position units and in the order the points were given. Null where the
/// survey is too small for the point to be checked at all.
/// </param>
/// <remarks>
/// <para>
/// Leave-one-out rather than the fit's own residual, which was the obvious thing and is useless
/// here: a spline passes through its points, and regularisation small enough to keep that promise
/// leaves misses around 1e-4 whether a point was marked well or badly -- measured, on a survey with
/// a third of the pan range of deliberate error in it. What that residual reports is the
/// conditioning of the kernel, not the operator's aim. Holding a point out asks the question the
/// operator actually has: does this point agree with the others? A hand slip cannot be reproduced
/// by the points around it, so it shows up at the size of the slip.
/// </para>
/// <para>
/// One bad point does raise every other number too, since it is still in the survey the others are
/// checked against. So this is a ranking, not a verdict on each point separately: re-check the
/// largest, fix or drop it, and the rest settle. Measured on a six-point survey with one point out
/// by 0.3 -- the culprit reads 0.30, its neighbours up to 0.23, and removing it drops them all
/// below 0.01.
/// </remarks>
public sealed record BindingFit(
  IReadOnlyList<BindingPoint> Points,
  double[] PanWeights,
  double[] TiltWeights,
  IReadOnlyList<float?> Residuals);

/// <summary>Where the fit says to look, and how much of a guess it is.</summary>
/// <param name="Extrapolated">
/// The asked-for point lies outside the marked area, where the surface is no longer interpolating
/// between measurements but continuing them. Not an error -- an object one step past the last
/// marked point is aimed at perfectly well -- but the further out, the less it means.
/// </param>
public sealed record BindingAim(float Pan, float Tilt, bool Extrapolated);

/// <summary>
/// Turns a handful of hand-marked correspondences into a mapping from the master's frame to the
/// slave's aim.
/// </summary>
/// <remarks>
/// <para>
/// A thin-plate spline, and deliberately not a geometric model. A homography would assume pan and
/// tilt behave like a projective plane, which is only approximately true (they are angles) and
/// falls apart across a wide sector; worse, it is a model, so a camera hung at an angle, with an
/// inverted axis or a non-linear tilt, is something it has to fight. The spline assumes nothing
/// about the axes -- not their direction, sign, orthogonality or linearity. Such a camera is just
/// different numbers in the measurements.
/// </para>
/// <para>
/// It also passes through the marked points, which matters to the operator: a place they marked is
/// a place the camera goes, not somewhere near it. With three points it degenerates to an affine
/// map, which is the right answer for three points.
/// </para>
/// <para>
/// The ground-plane assumption is not in this code, it is in the survey: the operator marks spots
/// on the ground, so the mapping learned is the one from ground spots to positions. That is why
/// aiming uses the bottom edge of an object's box.
/// </para>
/// </remarks>
public static class PtzSlaveBindingMath
{
  /// <summary>Below this the survey has no chance of pinning down an affine map, let alone a spline.</summary>
  public const int MinPoints = 3;

  /// <summary>
  /// How much the surface may miss a marked point in order to stay smooth.
  /// </summary>
  /// <remarks>
  /// Small on purpose. An operator's aim is worth a millimetre or two of slack -- exact
  /// interpolation bakes their hand tremor into the surface and makes it ring between points --
  /// but any more and "the camera goes where I marked" stops being true.
  /// </remarks>
  public const double DefaultLambda = 1e-3;

  // Two marked points closer than this in the master's frame are one point as far as the fit is
  // concerned: they contribute the same row twice, which is what makes the system singular.
  private const float MinSeparation = 1e-4f;

  // A survey lying along a line cannot say what happens across that line. The affine part of the
  // spline is exactly what goes undetermined, so this is checked before fitting rather than
  // discovered as a singular matrix.
  private const double MinCollinearArea = 1e-6;

  /// <summary>Fits the mapping, or says why the survey cannot carry one.</summary>
  /// <param name="lambda">Regularisation; 0 interpolates exactly.</param>
  public static bool TryFit(
    IReadOnlyList<BindingPoint> points,
    double lambda,
    out BindingFit? fit,
    out string? error)
  {
    fit = null;
    error = null;

    var known = points.Where(p => p.Pan.HasValue && p.Tilt.HasValue).ToList();
    if (known.Count < MinPoints)
    {
      error = $"At least {MinPoints} points with a readable position are needed, got {known.Count}";
      return false;
    }
    if (!Separated(known))
    {
      error = "Two or more points mark the same spot in the master's frame";
      return false;
    }
    if (!Spread(known))
    {
      error = "All points lie on one line -- mark the working area's perimeter, not a single line";
      return false;
    }

    var n = known.Count;
    var size = n + 3;
    var a = new double[size, size];

    for (var i = 0; i < n; i++)
    {
      for (var j = 0; j < n; j++)
        a[i, j] = i == j ? lambda : Kernel(Distance(known[i], known[j]));

      a[i, n] = 1.0;
      a[i, n + 1] = known[i].MasterX;
      a[i, n + 2] = known[i].MasterY;

      a[n, i] = 1.0;
      a[n + 1, i] = known[i].MasterX;
      a[n + 2, i] = known[i].MasterY;
    }

    var panRhs = new double[size];
    var tiltRhs = new double[size];
    for (var i = 0; i < n; i++)
    {
      panRhs[i] = known[i].Pan!.Value;
      tiltRhs[i] = known[i].Tilt!.Value;
    }

    // One matrix, two right-hand sides: pan and tilt are fitted over the same geometry and differ
    // only in what is being interpolated.
    if (!TrySolve(a, [panRhs, tiltRhs], out var solved))
    {
      error = "The marked points do not pin down a mapping -- spread them further apart";
      return false;
    }

    fit = new BindingFit(known, solved[0], solved[1], LeaveOneOut(known, lambda));
    return true;
  }

  // One fit per point, each without that point. N is what a human marked by hand, and this runs
  // when the survey is saved, so the cubic cost of a few extra solves is not worth avoiding.
  private static IReadOnlyList<float?> LeaveOneOut(
    IReadOnlyList<BindingPoint> known, double lambda)
  {
    var residuals = new float?[known.Count];
    if (known.Count <= MinPoints)
      // Three points fix an affine map exactly and have nothing left over to disagree with; drop
      // one and the remaining two cannot say anything at all.
      return residuals;

    for (var i = 0; i < known.Count; i++)
    {
      var rest = known.Where((_, j) => j != i).ToList();
      if (!TryFit(rest, lambda, out var withoutIt, out _))
        continue;

      var p = known[i];
      var aim = Project(withoutIt!, p.MasterX, p.MasterY);
      var dPan = aim.Pan - p.Pan!.Value;
      var dTilt = aim.Tilt - p.Tilt!.Value;
      residuals[i] = MathF.Sqrt((dPan * dPan) + (dTilt * dTilt));
    }
    return residuals;
  }

  /// <summary>Where the slave should look for a spot in the master's frame.</summary>
  public static BindingAim Project(BindingFit fit, float masterX, float masterY)
  {
    var pan = (float)Evaluate(fit.Points, fit.PanWeights, masterX, masterY);
    var tilt = (float)Evaluate(fit.Points, fit.TiltWeights, masterX, masterY);
    return new BindingAim(pan, tilt, !Inside(fit.Points, masterX, masterY));
  }

  /// <summary>
  /// How much of the object may remain in the zone the camera is already watching before it is
  /// worth moving to another.
  /// </summary>
  /// <remarks>
  /// The camera stays put while at least this share of the object's footprint is still in the
  /// current zone, so leaving takes a decisive move rather than a step. At a fifth, an object
  /// straddling a boundary two-to-one is left alone and one at four-to-one is followed -- which is
  /// about where a person reading the picture would say it had gone somewhere else.
  /// </remarks>
  public const float ZoneHoldShare = 0.2f;

  // How finely the footprint is cut up when apportioning it between zones. Twenty-one samples put
  // a boundary within five percent of the object's width, which is finer than the box itself is
  // repeatable between frames.
  private const int FootprintSamples = 21;

  /// <summary>
  /// Which marked point to aim at, weighing how much of the object lies in each zone.
  /// </summary>
  /// <remarks>
  /// <para>
  /// Zones meet along lines, and an object is not a point: a person standing on a boundary is
  /// genuinely in two zones at once, and a car crossing a junction of three can be in all of them.
  /// Judging by a single point -- the middle of the bottom edge -- makes that a coin toss decided
  /// by a few pixels of box jitter, and open-loop aiming obeys every toss, so the camera swings
  /// between presets while the object has not really gone anywhere.
  /// </para>
  /// <para>
  /// So the object's footprint is apportioned between the zones it covers, and the camera keeps
  /// watching the one it is already on while a fair share of the object is still there. It moves
  /// when another zone holds most of the object -- when the split is decisive rather than merely
  /// unequal. That is both steadier than a distance margin and easier to say out loud: the camera
  /// follows where the object mostly is.
  /// </para>
  /// <para>
  /// The footprint is the bottom edge of the box, which is the object's contact with the ground and
  /// the only part of it the survey has anything to say about.
  /// </para>
  /// </remarks>
  /// <param name="left">Left edge of the object's box in the master's frame.</param>
  /// <param name="right">Right edge of the object's box.</param>
  /// <param name="groundY">The bottom edge, where the object meets the ground.</param>
  /// <param name="currentPresetToken">The preset the camera was last sent to, or null.</param>
  /// <param name="holdShare">Share of the footprint that keeps the camera where it is.</param>
  public static BindingPoint? StickyZone(
    IReadOnlyList<BindingPoint> points,
    float left,
    float right,
    float groundY,
    string? currentPresetToken,
    float holdShare)
  {
    if (points.Count == 0)
      return null;

    var shares = new int[points.Count];
    var span = right - left;
    for (var i = 0; i < FootprintSamples; i++)
    {
      // A zero-width box degenerates to its single point rather than dividing by nothing.
      var x = FootprintSamples == 1 ? left : left + (span * i / (FootprintSamples - 1f));
      var at = NearestIndex(points, x, groundY);
      if (at >= 0)
        shares[at]++;
    }

    var winner = 0;
    for (var i = 1; i < points.Count; i++)
      if (shares[i] > shares[winner])
        winner = i;

    if (currentPresetToken is null)
      return points[winner];

    var currentIndex = -1;
    for (var i = 0; i < points.Count; i++)
      if (points[i].PresetToken == currentPresetToken)
        currentIndex = i;

    // The zone the camera sits in is gone -- its point was deleted or its preset removed -- so
    // there is nothing to be loyal to.
    if (currentIndex < 0)
      return points[winner];

    return shares[currentIndex] >= holdShare * FootprintSamples
      ? points[currentIndex]
      : points[winner];
  }

  private static int NearestIndex(IReadOnlyList<BindingPoint> points, float x, float y)
  {
    var best = -1;
    var bestDistance = double.MaxValue;
    for (var i = 0; i < points.Count; i++)
    {
      var dx = (double)points[i].MasterX - x;
      var dy = (double)points[i].MasterY - y;
      var distance = (dx * dx) + (dy * dy);
      if (distance >= bestDistance)
        continue;
      bestDistance = distance;
      best = i;
    }
    return best;
  }

  /// <summary>The marked point nearest a spot in the master's frame -- the presets method.</summary>
  public static BindingPoint? Nearest(IReadOnlyList<BindingPoint> points, float masterX, float masterY)
  {
    BindingPoint? best = null;
    var bestDistance = double.MaxValue;
    foreach (var p in points)
    {
      var dx = p.MasterX - masterX;
      var dy = p.MasterY - masterY;
      var distance = (dx * dx) + (dy * dy);
      if (distance >= bestDistance)
        continue;
      bestDistance = distance;
      best = p;
    }
    return best;
  }

  // U(r) = r^2 ln r, the thin-plate kernel: the bending energy of a metal sheet pinned at the
  // marked points. r = 0 is the removable singularity at a point's own row.
  private static double Kernel(double r) => r <= 0 ? 0 : r * r * Math.Log(r);

  private static double Distance(BindingPoint a, BindingPoint b)
  {
    var dx = (double)a.MasterX - b.MasterX;
    var dy = (double)a.MasterY - b.MasterY;
    return Math.Sqrt((dx * dx) + (dy * dy));
  }

  private static double Evaluate(
    IReadOnlyList<BindingPoint> points, double[] weights, float x, float y)
  {
    var n = points.Count;
    var value = weights[n] + (weights[n + 1] * x) + (weights[n + 2] * y);
    for (var i = 0; i < n; i++)
    {
      var dx = (double)points[i].MasterX - x;
      var dy = (double)points[i].MasterY - y;
      value += weights[i] * Kernel(Math.Sqrt((dx * dx) + (dy * dy)));
    }
    return value;
  }

  private static bool Separated(IReadOnlyList<BindingPoint> points)
  {
    for (var i = 0; i < points.Count; i++)
      for (var j = i + 1; j < points.Count; j++)
        if (Distance(points[i], points[j]) < MinSeparation)
          return false;
    return true;
  }

  // Twice the largest triangle any three points span. Zero means a line.
  private static bool Spread(IReadOnlyList<BindingPoint> points)
  {
    for (var i = 0; i < points.Count; i++)
      for (var j = i + 1; j < points.Count; j++)
        for (var k = j + 1; k < points.Count; k++)
        {
          var area = Math.Abs(
            ((double)points[j].MasterX - points[i].MasterX) * (points[k].MasterY - points[i].MasterY) -
            ((double)points[k].MasterX - points[i].MasterX) * (points[j].MasterY - points[i].MasterY));
          if (area > MinCollinearArea)
            return true;
        }
    return false;
  }

  // Gauss with partial pivoting. The matrix is (N+3) square with N under thirty in any survey a
  // human marks by hand, so nothing more considered is warranted -- and the whole solve happens
  // when the configuration changes, not per detection.
  private static bool TrySolve(double[,] matrix, double[][] rhs, out double[][] solutions)
  {
    var n = matrix.GetLength(0);
    var a = (double[,])matrix.Clone();
    var b = rhs.Select(r => (double[])r.Clone()).ToArray();
    solutions = [];

    for (var col = 0; col < n; col++)
    {
      var pivot = col;
      for (var row = col + 1; row < n; row++)
        if (Math.Abs(a[row, col]) > Math.Abs(a[pivot, col]))
          pivot = row;

      if (Math.Abs(a[pivot, col]) < 1e-12)
        return false;

      if (pivot != col)
      {
        for (var c = 0; c < n; c++)
          (a[col, c], a[pivot, c]) = (a[pivot, c], a[col, c]);
        foreach (var r in b)
          (r[col], r[pivot]) = (r[pivot], r[col]);
      }

      for (var row = col + 1; row < n; row++)
      {
        var factor = a[row, col] / a[col, col];
        if (factor == 0) continue;
        for (var c = col; c < n; c++)
          a[row, c] -= factor * a[col, c];
        foreach (var r in b)
          r[row] -= factor * r[col];
      }
    }

    solutions = new double[b.Length][];
    for (var s = 0; s < b.Length; s++)
    {
      var x = new double[n];
      for (var row = n - 1; row >= 0; row--)
      {
        var sum = b[s][row];
        for (var c = row + 1; c < n; c++)
          sum -= a[row, c] * x[c];
        x[row] = sum / a[row, row];
      }
      solutions[s] = x;
    }
    return true;
  }

  // Inside the convex hull of the survey, by the sign of the cross product against each hull edge.
  private static bool Inside(IReadOnlyList<BindingPoint> points, float x, float y)
  {
    var hull = ConvexHull(points);
    if (hull.Count < 3)
      return false;

    for (var i = 0; i < hull.Count; i++)
    {
      var a = hull[i];
      var b = hull[(i + 1) % hull.Count];
      var cross =
        ((double)b.MasterX - a.MasterX) * (y - a.MasterY) -
        ((double)b.MasterY - a.MasterY) * (x - a.MasterX);
      // The hull is built counter-clockwise, so an inside point is left of every edge.
      if (cross < -1e-9)
        return false;
    }
    return true;
  }

  // Andrew's monotone chain, counter-clockwise. Small N, and it runs once per aim at most.
  private static List<BindingPoint> ConvexHull(IReadOnlyList<BindingPoint> points)
  {
    var sorted = points
      .OrderBy(p => p.MasterX)
      .ThenBy(p => p.MasterY)
      .ToList();
    if (sorted.Count < 3)
      return sorted;

    var lower = Chain(sorted);
    var upper = Chain(Enumerable.Reverse(sorted).ToList());
    // Each chain ends where the other begins, so both endpoints are dropped once.
    lower.RemoveAt(lower.Count - 1);
    upper.RemoveAt(upper.Count - 1);
    lower.AddRange(upper);
    return lower;
  }

  private static List<BindingPoint> Chain(IReadOnlyList<BindingPoint> ordered)
  {
    var chain = new List<BindingPoint>();
    foreach (var p in ordered)
    {
      while (chain.Count >= 2 && Cross(chain[^2], chain[^1], p) <= 0)
        chain.RemoveAt(chain.Count - 1);
      chain.Add(p);
    }
    return chain;
  }

  private static double Cross(BindingPoint o, BindingPoint a, BindingPoint b) =>
    ((double)a.MasterX - o.MasterX) * ((double)b.MasterY - o.MasterY) -
    ((double)a.MasterY - o.MasterY) * ((double)b.MasterX - o.MasterX);
}
