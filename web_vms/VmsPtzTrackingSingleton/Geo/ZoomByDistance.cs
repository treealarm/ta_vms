namespace VmsPtzTrackingSingleton.Geo;

/// <summary>A zoom the operator chose for an object at a known distance from the camera.</summary>
public readonly record struct ZoomSample(double DistanceM, float Zoom);

/// <summary>
/// How far a slave zooms for an object at a given distance, read off the samples the operator
/// recorded at landmarks.
/// </summary>
/// <remarks>
/// <para>
/// Learned rather than computed from the lens, because nothing tells us the lens: ONVIF reports
/// zoom in the camera's own units, and how those units map to a field of view differs between
/// models -- linear in magnification on some, equal ratios per step on others. A sample says
/// directly what the operator wanted to see at that distance, margins included, in whatever units
/// the camera answers in.
/// </para>
/// <para>
/// Interpolated in the logarithm of distance. An object keeps its size in the frame when the focal
/// length grows in proportion to its distance, and on a lens whose zoom steps are equal ratios --
/// the common design, and the simulator's -- that makes zoom a straight line in ln(distance), so
/// two samples are exact there. On any other lens the error is bounded by the spacing of the
/// samples, which is the operator's to tighten.
/// </para>
/// <para>
/// Never extrapolated: below the nearest sample and beyond the farthest the zoom stays at theirs.
/// The samples are therefore the range the camera may use -- an object closer than anything
/// sampled is framed wider than intended, never cropped by a guess.
/// </para>
/// </remarks>
public sealed class ZoomByDistance
{
  private readonly ZoomSample[] _samples;

  private ZoomByDistance(ZoomSample[] samples)
  {
    _samples = samples;
    Widest = samples.Min(s => s.Zoom);
  }

  /// <summary>The widest zoom among the samples: what to use where the aim is not to be trusted.</summary>
  public float Widest { get; }

  /// <returns>Null when there are no usable samples: the camera is then not zoomed at all.</returns>
  public static ZoomByDistance? TryBuild(IEnumerable<ZoomSample> samples)
  {
    var usable = samples
      .Where(s => s.DistanceM > 0 && double.IsFinite(s.DistanceM) && float.IsFinite(s.Zoom))
      .OrderBy(s => s.DistanceM)
      .ToArray();
    return usable.Length == 0 ? null : new ZoomByDistance(usable);
  }

  /// <summary>The zoom for an object this far from the camera.</summary>
  public float Zoom(double distanceM)
  {
    if (!(distanceM > _samples[0].DistanceM))
      return _samples[0].Zoom;
    if (distanceM >= _samples[^1].DistanceM)
      return _samples[^1].Zoom;

    var i = 1;
    while (_samples[i].DistanceM < distanceM)
      i++;
    var (near, far) = (_samples[i - 1], _samples[i]);
    // Two landmarks at the same distance: no line through them, either zoom is as good.
    var span = Math.Log(far.DistanceM / near.DistanceM);
    if (span <= 0)
      return near.Zoom;
    var t = Math.Log(distanceM / near.DistanceM) / span;
    return (float)(near.Zoom + (t * (far.Zoom - near.Zoom)));
  }
}
