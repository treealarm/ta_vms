using Vms.Cfg;
using VmsSquareSingleton;

namespace VmsSquareSingleton.Tests;

public class SquareSpaceExportTests
{
  private static GeoPoint At(double lat, double lon) => new() { Lat = lat, Lon = lon };

  /// A plan whose picture is laid on the given corners.
  private static Space Plan(string id, string? parent, uint width, uint height, GeoPoint tl, GeoPoint tr, GeoPoint bl, params GeoPoint[] footprint)
  {
    var space = new Space
    {
      Id = id, Name = id, Kind = SpaceKind.Plan, ParentId = parent ?? string.Empty,
      Plan = new SpacePlan { WidthPx = width, HeightPx = height, Mime = "image/png", Etag = "e", Corners = new PlanCorners { TopLeft = tl, TopRight = tr, BottomLeft = bl } },
    };
    space.Footprint.AddRange(footprint);
    return space;
  }

  /// The WGS84 point of a pixel of a plan, as the client lays the picture.
  private static (double Lat, double Lon) PixelToGeo(Space s, double x, double y)
  {
    var c = s.Plan.Corners;
    double u = x / s.Plan.WidthPx, v = y / s.Plan.HeightPx;
    return (c.TopLeft.Lat + u * (c.TopRight.Lat - c.TopLeft.Lat) + v * (c.BottomLeft.Lat - c.TopLeft.Lat),
            c.TopLeft.Lon + u * (c.TopRight.Lon - c.TopLeft.Lon) + v * (c.BottomLeft.Lon - c.TopLeft.Lon));
  }

  [Fact]
  public void APictureCornerLandsOnTheFrameCorner()
  {
    var plan = Plan("p", null, 800, 600, At(0.001, 0), At(0.001, 0.002), At(0, 0));
    var frame = SquareSpaceGeometry.BuildFrames([plan])["p"];

    Assert.Equal(new DiagramRect(0, 0, 800, 600), frame.Geometry);
    AssertNear((0, 0), frame.ToFrame(0.001, 0));
    AssertNear((800, 0), frame.ToFrame(0.001, 0.002));
    AssertNear((0, 600), frame.ToFrame(0, 0));
    AssertNear((800, 600), frame.ToFrame(0, 0.002));
  }

  [Fact]
  public void ARotatedPictureIsUnturnedIntoItsPixels()
  {
    // Turned 30° on the map: Square shows the picture upright, so the pixel is what matters.
    var a = 30 * Math.PI / 180;
    GeoPoint Turn(double east, double north) => At(north * Math.Cos(a) + east * Math.Sin(a), east * Math.Cos(a) - north * Math.Sin(a));
    var plan = Plan("p", null, 1200, 900, Turn(0, 0.0009), Turn(0.0012, 0.0009), Turn(0, 0));
    var frame = SquareSpaceGeometry.BuildFrames([plan])["p"];

    foreach (var (x, y) in new[] { (100.0, 50.0), (600.0, 450.0), (1100.0, 880.0) })
    {
      var (lat, lon) = PixelToGeo(plan, x, y);
      AssertNear((x, y), frame.ToFrame(lat, lon));
    }
  }

  [Fact]
  public void APlanWithoutAPictureIsLaidOutFromItsView()
  {
    // Twice as wide as it is tall, in metres, at the equator.
    var plan = new Space
    {
      Id = "p", Name = "p", Kind = SpaceKind.Plan,
      View = new GeoBounds { SouthWest = At(0, 0), NorthEast = At(0.0005, 0.001) },
    };
    var frame = SquareSpaceGeometry.BuildFrames([plan])["p"];

    Assert.Equal(1000, frame.Geometry.Width, 6);
    Assert.Equal(500, frame.Geometry.Height, 3);
    AssertNear((0, 0), frame.ToFrame(0.0005, 0));
    AssertNear((500, 250), frame.ToFrame(0.00025, 0.0005));
  }

  [Fact]
  public void ANestedPlanSitsOnTheBoxOfItsFootprintAndItsInsideIsStretchedToIt()
  {
    var site = Plan("site", null, 1000, 1000, At(0.001, 0), At(0.001, 0.001), At(0, 0));
    GeoPoint OnSite(double x, double y) { var (lat, lon) = PixelToGeo(site, x, y); return At(lat, lon); }
    // Its own picture is 400×600, in its own made-up coordinates.
    var floor = Plan("floor", "site", 400, 600, At(0.0006, 0), At(0.0006, 0.0004), At(0, 0),
      OnSite(100, 200), OnSite(300, 200), OnSite(300, 500), OnSite(100, 500));

    var frames = SquareSpaceGeometry.BuildFrames([floor, site]);

    var g = frames["floor"].Geometry;
    AssertNear((100, 200), (g.Left, g.Top));
    AssertNear((200, 300), (g.Width, g.Height));
    // The floor's pixel (200, 300) is the middle of its picture, half of 400×600 scaled to 200×300.
    var (lat, lon) = PixelToGeo(floor, 200, 300);
    AssertNear((100, 150), frames["floor"].ToFrame(lat, lon));
  }

  [Fact]
  public void APlanInsideAMapIsADiagramOfItsOwnReachedFromItsOutline()
  {
    var map = new Space { Id = "map", Name = "map", Kind = SpaceKind.Map, RealCoordinates = true };
    var yard = Plan("yard", "map", 800, 600, At(41.31, 69.24), At(41.31, 69.25), At(41.30, 69.24),
      At(41.31, 69.24), At(41.31, 69.25), At(41.30, 69.25));

    var batch = SquareSpaceExport.Build([map, yard], [], new Dictionary<string, string>(),
      new Dictionary<string, string> { ["yard"] = "static_files/vms/plans/yard.png" }, "vms");

    var diagram = Assert.Single(batch.Diagrams.Diagrams);
    Assert.Equal(SquareCameraPush.ToObjectId("yard"), diagram.Id);
    Assert.Equal((0d, 0d, 800d, 600d), (diagram.Geometry.Left, diagram.Geometry.Top, diagram.Geometry.Width, diagram.Geometry.Height));
    Assert.Equal("static_files/vms/plans/yard.png", diagram.BackgroundImg);
    var outline = Assert.Single(batch.Figures.Figs);
    Assert.Equal("Polygon", outline.Geometry.Type);
    Assert.Equal(3, outline.Geometry.Coord.Count);
    // Parents before children, under the producer's root.
    Assert.Equal([SquareCameraPush.ToObjectId("map"), SquareCameraPush.ToObjectId("yard")], batch.Objects.Objects.Select(o => o.Id));
    Assert.Equal(SquareCameraPush.ToObjectId("vms"), batch.Objects.Objects[0].ParentId);
  }

  [Fact]
  public void TheCameraIsDrawnWhereItStandsAndAReplicaWhereItIsPictured()
  {
    var map = new Space { Id = "map", Name = "map", Kind = SpaceKind.Map, RealCoordinates = true };
    var room = Plan("room", null, 1000, 1000, At(0.001, 0), At(0.001, 0.001), At(0, 0));
    var stands = new CameraPlacement { CameraId = "cam", SpaceId = "room", IsReference = true, Lat = 0.0005, Lon = 0.0005 };
    var onMap = new CameraPlacement { CameraId = "cam", SpaceId = "map", IsReference = false, Lat = 41.3, Lon = 69.2 };

    var batch = SquareSpaceExport.Build([map, room], [stands, onMap],
      new Dictionary<string, string> { ["cam"] = "Gate" }, new Dictionary<string, string>(), "vms");

    var icon = Assert.Single(batch.Diagrams.Diagrams, d => d.Id == SquareCameraPush.ToObjectId("cam"));
    Assert.Equal(SquareSpaceExport.CameraDiagramType, icon.DgrType);
    AssertNear((480, 480), (icon.Geometry.Left, icon.Geometry.Top));
    var replica = Assert.Single(batch.Objects.Objects, o => o.Id == SquareSpaceExport.PictureId(onMap));
    Assert.Equal(SquareCameraPush.ToObjectId("cam"), replica.OwnerId);
    Assert.Equal(SquareCameraPush.ToObjectId("map"), replica.ParentId);
    Assert.Equal("Gate", replica.Name);
    var point = Assert.Single(batch.Figures.Figs, f => f.Id == replica.Id);
    Assert.Equal("Point", point.Geometry.Type);
    // The camera object itself is not in the space export: its own push carries it.
    Assert.DoesNotContain(batch.Objects.Objects, o => o.Id == SquareCameraPush.ToObjectId("cam"));
  }

  private static void AssertNear((double X, double Y) expected, (double X, double Y) actual)
  {
    Assert.Equal(expected.X, actual.X, 3);
    Assert.Equal(expected.Y, actual.Y, 3);
  }
}
