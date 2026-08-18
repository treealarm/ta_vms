namespace VmsFs;

/// <summary>
/// How much of a clip the detector had something to say about.
///
/// Its own file, and taking plain timestamps rather than boxes, because it is
/// interval arithmetic and nothing more: no database, no protobuf, no FFmpeg.
/// That is what makes it checkable on its own, which matters — this number
/// decides whether a clip gets rewritten at all.
/// </summary>
public static class RoiCoverage
{
  /// <summary>
  /// Fraction of [0, durationSec] covered by the union of [t - hold, t + hold]
  /// around each timestamp.
  ///
  /// The union is the point. Boxes arrive one per object and several objects
  /// share an instant, so adding their windows up would count the same second
  /// once per object and report a busy scene as far more than fully covered.
  /// </summary>
  public static double Fraction(IReadOnlyList<double> timesSec, double durationSec, double holdSec)
  {
    if (timesSec.Count == 0 || durationSec <= 0)
      return 0;

    var hold = holdSec > 0 ? holdSec : 0;
    var spans = timesSec
      .Select(t => (Start: Math.Max(0, t - hold), End: Math.Min(durationSec, t + hold)))
      // Drops what falls outside the clip entirely, and the degenerate case of
      // hold 0, where a window has no width to cover anything with.
      .Where(s => s.End > s.Start)
      .OrderBy(s => s.Start)
      .ToList();
    if (spans.Count == 0)
      return 0;

    double covered = 0;
    var start = spans[0].Start;
    var end = spans[0].End;
    foreach (var span in spans.Skip(1))
    {
      if (span.Start <= end)
      {
        end = Math.Max(end, span.End);
        continue;
      }
      covered += end - start;
      start = span.Start;
      end = span.End;
    }
    covered += end - start;

    return Math.Min(1.0, covered / durationSec);
  }
}
