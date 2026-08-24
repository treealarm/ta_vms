namespace VmsFs;

/// <summary>
/// How much of a clip is under a region — in pixels as well as in time.
///
/// Its own file, and taking plain numbers rather than protobuf boxes, because it is
/// geometry and interval arithmetic and nothing more: no database, no protobuf, no
/// FFmpeg. That is what makes it checkable on its own, which matters — this number
/// decides whether a clip gets rewritten at all.
/// </summary>
/// <remarks>
/// This used to measure time alone: the fraction of the clip's duration during which the
/// detector had said <em>something</em>. That is the wrong predictor and the archive proved
/// it. Re-compression saves by spending fewer bits on background, so what decides whether
/// there is anything to save is how much of the frame is <em>not</em> background. Measured
/// across this deployment's archive, a person box averages 41.5% of the frame and every one
/// of 107 clips carrying CV boxes was over a quarter of the frame on average — protect that
/// and there is no background left to take the bits from, which is exactly why re-encoding
/// them produced larger files.
///
/// So the number is now pixel-seconds: the share of the frame under a region, averaged over
/// the clip's whole duration. It subsumes the old measure rather than replacing it — an
/// instant with no boxes contributes zero, so a clip whose objects appear for a tenth of its
/// length cannot score higher than 0.1 no matter how large they are.
/// </remarks>
public static class RoiCoverage
{
  /// <summary>One box on the timeline, in normalized frame coordinates (0..1).</summary>
  public readonly record struct Box(double T, double X, double Y, double W, double H);

  /// <summary>
  /// Mean fraction of the frame under a region, averaged over [0, durationSec].
  ///
  /// Each box applies for <paramref name="holdSec"/> either side of its own sample — the
  /// same window the encoder is given, so the number describes the map the encoder will
  /// actually build rather than an idealised one.
  /// </summary>
  public static double ProtectedShare(
    IReadOnlyList<Box> boxes, double durationSec, double holdSec)
  {
    if (boxes.Count == 0 || durationSec <= 0)
      return 0;

    var hold = holdSec > 0 ? holdSec : 0;
    if (hold <= 0)
      return 0;  // a window with no width covers no time, whatever its area

    // The active set only changes where a window opens or closes, so between two consecutive
    // boundaries the union area is constant and one evaluation at the midpoint is exact.
    var bounds = new SortedSet<double> { 0, durationSec };
    foreach (var b in boxes)
    {
      var start = Math.Max(0, b.T - hold);
      var end = Math.Min(durationSec, b.T + hold);
      if (end <= start)
        continue;
      bounds.Add(start);
      bounds.Add(end);
    }

    var edges = bounds.ToList();
    double pixelSeconds = 0;
    for (var i = 0; i + 1 < edges.Count; i++)
    {
      var (from, to) = (edges[i], edges[i + 1]);
      if (to <= from)
        continue;
      var mid = (from + to) / 2;
      var active = boxes.Where(b => Math.Abs(b.T - mid) <= hold).ToList();
      if (active.Count == 0)
        continue;
      pixelSeconds += (to - from) * UnionArea(active);
    }

    return Math.Min(1.0, pixelSeconds / durationSec);
  }

  /// <summary>
  /// Area of the union of these boxes, as a fraction of the frame.
  ///
  /// The union is the point, and doubly so here: boxes nest (a face inside a person, a plate
  /// inside a vehicle) and several objects share an instant, so adding areas up would report
  /// a scene with one person in it as more than fully covered.
  ///
  /// Exact rather than rasterized — a sweep over the distinct x edges, merging y intervals
  /// within each slab. A clip's instant holds a handful of boxes, so exactness is free and it
  /// spares the reader having to reason about a grid's error term.
  /// </summary>
  public static double UnionArea(IReadOnlyList<Box> boxes)
  {
    var rects = boxes
      .Select(b => (
        X1: Math.Clamp(b.X, 0, 1), Y1: Math.Clamp(b.Y, 0, 1),
        X2: Math.Clamp(b.X + b.W, 0, 1), Y2: Math.Clamp(b.Y + b.H, 0, 1)))
      .Where(r => r.X2 > r.X1 && r.Y2 > r.Y1)
      .ToList();
    if (rects.Count == 0)
      return 0;

    var xs = rects.SelectMany(r => new[] { r.X1, r.X2 }).Distinct().OrderBy(x => x).ToList();

    double area = 0;
    for (var i = 0; i + 1 < xs.Count; i++)
    {
      var width = xs[i + 1] - xs[i];
      if (width <= 0)
        continue;
      var slabMid = (xs[i] + xs[i + 1]) / 2;

      // Merged length of the y intervals of every rectangle crossing this slab.
      var spans = rects
        .Where(r => r.X1 <= slabMid && slabMid <= r.X2)
        .Select(r => (Start: r.Y1, End: r.Y2))
        .OrderBy(s => s.Start)
        .ToList();
      if (spans.Count == 0)
        continue;

      double covered = 0;
      var (start, end) = (spans[0].Start, spans[0].End);
      foreach (var span in spans.Skip(1))
      {
        if (span.Start <= end)
        {
          end = Math.Max(end, span.End);
          continue;
        }
        covered += end - start;
        (start, end) = (span.Start, span.End);
      }
      covered += end - start;

      area += width * covered;
    }

    return Math.Min(1.0, area);
  }
}
