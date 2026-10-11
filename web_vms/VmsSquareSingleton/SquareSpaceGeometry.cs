using Vms.Cfg;

namespace VmsSquareSingleton;

/// <summary>A rectangle on a Square diagram, in the pixels of the diagram it is drawn on.</summary>
public readonly record struct DiagramRect(double Left, double Top, double Width, double Height);

/// <summary>
/// How a plan space is drawn as a Square diagram: the rectangle it takes on its parent (or its own
/// paper, at the top), and where a point of the space lands inside it.
/// </summary>
/// <remarks>
/// A Square diagram places its children in pixels of its own <c>geometry</c> — the same rectangle
/// that places it on its parent — and stretches its background image over that rectangle. A space
/// has a natural frame of its own instead: the pixels of its plan image, or, with no image, its
/// default view laid out <see cref="SquareSpaceGeometry.FrameWidthWithoutImage"/> pixels wide. A point
/// goes from WGS84 to the natural frame, then is stretched by the same factors the background is,
/// so it stays on the spot of the picture it was put on.
/// </remarks>
public sealed class SpaceFrame
{
  private readonly Func<double, double, (double X, double Y)> _toNatural;
  private readonly double _scaleX;
  private readonly double _scaleY;

  internal SpaceFrame(double naturalWidth, double naturalHeight, Func<double, double, (double X, double Y)> toNatural, DiagramRect geometry)
  {
    _toNatural = toNatural;
    Geometry = geometry;
    _scaleX = geometry.Width / naturalWidth;
    _scaleY = geometry.Height / naturalHeight;
  }

  /// <summary>Where the space is drawn on its parent diagram; at the top, its own paper from (0, 0).</summary>
  public DiagramRect Geometry { get; }

  /// <summary>A point of this space, in the pixels its children are placed in.</summary>
  public (double X, double Y) ToFrame(double lat, double lon)
  {
    var (x, y) = _toNatural(lat, lon);
    return (x * _scaleX, y * _scaleY);
  }
}

/// <summary>The geometry of exporting ta_vms spaces as Square diagrams; pure, so it can be tested without Square.</summary>
public static class SquareSpaceGeometry
{
  /// <summary>The width a plan without an image is laid out at, in pixels.</summary>
  public const double FrameWidthWithoutImage = 1000;

  /// <summary>A camera's icon on a diagram, the size Square gives one dropped there by hand.</summary>
  public const double IconSize = 40;

  /// <summary>A nested plan with no footprint is put in the parent's corner, at this share of its width.</summary>
  private const double UnplacedShare = 0.2;

  private const double DegToRad = Math.PI / 180;

  /// <summary>
  /// The frame of every plan space, keyed by space id. Maps have none: a map is Square's own map,
  /// and a plan inside one is a diagram of its own, reached from its footprint there.
  /// </summary>
  public static IReadOnlyDictionary<string, SpaceFrame> BuildFrames(IReadOnlyCollection<Space> spaces)
  {
    var byId = spaces.ToDictionary(s => s.Id, StringComparer.Ordinal);
    var frames = new Dictionary<string, SpaceFrame>(StringComparer.Ordinal);

    SpaceFrame? Frame(Space space, int depth)
    {
      if (space.Kind != SpaceKind.Plan)
        return null;
      if (frames.TryGetValue(space.Id, out var known))
        return known;

      var (width, height, toNatural) = Natural(space);
      var parent = !string.IsNullOrEmpty(space.ParentId) && depth < spaces.Count
        ? byId.GetValueOrDefault(space.ParentId)
        : null;
      var parentFrame = parent is null ? null : Frame(parent, depth + 1);

      var geometry = parentFrame is null
        ? new DiagramRect(0, 0, width, height)
        : PlaceOn(parentFrame, space, width, height);
      var frame = new SpaceFrame(width, height, toNatural, geometry);
      frames[space.Id] = frame;
      return frame;
    }

    foreach (var space in spaces)
      Frame(space, 0);
    return frames;
  }

  /// <summary>An icon centred on a point of a space.</summary>
  public static DiagramRect IconAt(SpaceFrame frame, double lat, double lon)
  {
    var (x, y) = frame.ToFrame(lat, lon);
    return new DiagramRect(x - IconSize / 2, y - IconSize / 2, IconSize, IconSize);
  }

  /// <summary>The natural frame of a plan space and the way from WGS84 into it.</summary>
  private static (double Width, double Height, Func<double, double, (double X, double Y)> ToNatural) Natural(Space space)
  {
    if (space.Plan is { WidthPx: > 0, HeightPx: > 0, Corners: { } c })
    {
      // The plan's pixel (x, y) is TL + x/W·(TR − TL) + y/H·(BL − TL), an affine map; inverted here.
      // Degrees are fine for it: an affine map stays affine whatever the units.
      double w = space.Plan.WidthPx, h = space.Plan.HeightPx;
      double aLat = c.TopRight.Lat - c.TopLeft.Lat, aLon = c.TopRight.Lon - c.TopLeft.Lon;
      double bLat = c.BottomLeft.Lat - c.TopLeft.Lat, bLon = c.BottomLeft.Lon - c.TopLeft.Lon;
      var det = aLat * bLon - aLon * bLat;
      if (Math.Abs(det) > 1e-18)
      {
        return (w, h, (lat, lon) =>
        {
          double dLat = lat - c.TopLeft.Lat, dLon = lon - c.TopLeft.Lon;
          var u = (dLat * bLon - dLon * bLat) / det;
          var v = (aLat * dLon - aLon * dLat) / det;
          return (u * w, v * h);
        });
      }
    }

    // No picture: the default view, north up, its width FrameWidthWithoutImage pixels and its
    // height in proportion to the metres it covers.
    var view = space.View;
    double south = view?.SouthWest?.Lat ?? -0.0005, west = view?.SouthWest?.Lon ?? -0.0005;
    double north = view?.NorthEast?.Lat ?? 0.0005, east = view?.NorthEast?.Lon ?? 0.0005;
    var cos = Math.Cos((south + north) / 2 * DegToRad);
    var widthM = Math.Max(1e-9, (east - west) * cos);
    var heightM = Math.Max(1e-9, north - south);
    var frameHeight = FrameWidthWithoutImage * heightM / widthM;
    return (FrameWidthWithoutImage, frameHeight, (lat, lon) =>
      ((lon - west) / (east - west) * FrameWidthWithoutImage, (north - lat) / (north - south) * frameHeight));
  }

  /// <summary>Where a nested plan goes on its parent: the box round its footprint.</summary>
  private static DiagramRect PlaceOn(SpaceFrame parent, Space space, double naturalWidth, double naturalHeight)
  {
    if (space.Footprint.Count < 3)
    {
      var w = parent.Geometry.Width * UnplacedShare;
      return new DiagramRect(0, 0, w, w * naturalHeight / naturalWidth);
    }

    var points = space.Footprint.Select(p => parent.ToFrame(p.Lat, p.Lon)).ToList();
    var left = points.Min(p => p.X);
    var top = points.Min(p => p.Y);
    return new DiagramRect(left, top, points.Max(p => p.X) - left, points.Max(p => p.Y) - top);
  }
}
