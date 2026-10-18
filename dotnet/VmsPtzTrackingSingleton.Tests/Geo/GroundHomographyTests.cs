using VmsPtzTrackingSingleton.Geo;

namespace VmsPtzTrackingSingleton.Tests.Geo;

public class GroundHomographyTests
{
  /// <summary>
  /// A pinhole camera 6 m up, turned 20° east of north and tilted 30° down, with a 16:9 picture:
  /// where a point of the ground appears in its normalised frame.
  /// </summary>
  private static (double X, double Y) Picture(Enu ground)
  {
    const double heightM = 6;
    var yaw = 20 * Math.PI / 180;
    var pitch = 30 * Math.PI / 180;
    const double fx = 0.8;
    const double fy = 0.8 * 16 / 9;

    double[] forward = [Math.Sin(yaw) * Math.Cos(pitch), Math.Cos(yaw) * Math.Cos(pitch), -Math.Sin(pitch)];
    double[] right = [Math.Cos(yaw), -Math.Sin(yaw), 0];
    double[] down =
    [
      forward[1] * right[2] - forward[2] * right[1],
      forward[2] * right[0] - forward[0] * right[2],
      forward[0] * right[1] - forward[1] * right[0],
    ];
    double[] v = [ground.East, ground.North, -heightM];
    double Dot(double[] a) => a[0] * v[0] + a[1] * v[1] + a[2] * v[2];

    var z = Dot(forward);
    return (0.5 + fx * Dot(right) / z, 0.5 + fy * Dot(down) / z);
  }

  private static GroundSample Mark(string id, double east, double north, double floorM = 0)
  {
    var ground = new Enu(east, north);
    var (x, y) = Picture(ground);
    return new GroundSample(id, x, y, ground, floorM);
  }

  private static readonly GroundSample[] Corners =
  [
    Mark("a", -2, 8), Mark("b", 6, 6), Mark("c", 10, 20), Mark("d", -4, 22),
  ];

  [Fact]
  public void FourMarksMapThePictureOntoTheGround()
  {
    Assert.True(GroundHomography.TryFit(Corners, out var fit, out var error), error);

    foreach (var expected in new[] { new Enu(0, 10), new Enu(3, 15), new Enu(8, 25), new Enu(-6, 30) })
    {
      var (x, y) = Picture(expected);
      var actual = fit!.Project(x, y);
      Assert.NotNull(actual);
      Assert.Equal(expected.East, actual.Value.East, 6);
      Assert.Equal(expected.North, actual.Value.North, 6);
    }
    // Four points fix it exactly, so there is nothing to check them against.
    Assert.All(fit!.Residuals.Values, Assert.Null);
  }

  [Fact]
  public void AFifthMarkLetsThemBeChecked()
  {
    GroundSample[] marks = [.. Corners, Mark("e", 2, 14)];
    Assert.True(GroundHomography.TryFit(marks, out var fit, out var error), error);

    Assert.Equal(5, fit!.Residuals.Count);
    Assert.All(fit.Residuals.Values, r => Assert.InRange(r!.Value, 0, 1e-6));
  }

  [Fact]
  public void AMisplacedMarkStandsOutInTheResiduals()
  {
    var wrong = Mark("e", 2, 14) with { Ground = new Enu(5, 14) };
    GroundSample[] marks = [.. Corners, Mark("f", 0, 18), wrong];
    Assert.True(GroundHomography.TryFit(marks, out var fit, out var error), error);

    var worst = fit!.Residuals.MaxBy(r => r.Value ?? 0).Key;
    Assert.Equal("e", worst);
  }

  [Fact]
  public void TheSkyIsNotOnTheGround()
  {
    Assert.True(GroundHomography.TryFit(Corners, out var fit, out var error), error);

    // 30° down with this lens puts the horizon well above the top of the frame.
    Assert.Null(fit!.Project(0.5, -1));
  }

  [Fact]
  public void TheWgs84MatrixLandsWhereTheGroundDoes()
  {
    Assert.True(GroundHomography.TryFit(Corners, out var fit, out var error), error);
    var frame = new EnuFrame(41.3, 69.24);
    var m = fit!.ToWgs84(frame);

    var (x, y) = Picture(new Enu(3, 15));
    var w = m[6] * x + m[7] * y + m[8];
    var expected = frame.FromEnu(fit.Project(x, y)!.Value);

    Assert.True(w > 0);
    Assert.Equal(expected.Lat, (m[0] * x + m[1] * y + m[2]) / w, 9);
    Assert.Equal(expected.Lon, (m[3] * x + m[4] * y + m[5]) / w, 9);
  }

  [Fact]
  public void NeedsFourMarks()
  {
    Assert.False(GroundHomography.TryFit(Corners[..3], out _, out var error));
    Assert.Contains("at least 4", error);
  }

  [Fact]
  public void RefusesMarksAlongOneLine()
  {
    GroundSample[] marks = [Mark("a", 0, 8), Mark("b", 1, 12), Mark("c", 2, 16), Mark("d", 3, 20)];
    Assert.False(GroundHomography.TryFit(marks, out _, out var error));
    Assert.Contains("one line", error);
  }

  [Fact]
  public void RefusesThreeOfFourOnOneLine()
  {
    GroundSample[] marks = [Mark("a", 0, 8), Mark("b", 2, 12), Mark("c", 4, 16), Mark("d", -6, 20)];
    Assert.False(GroundHomography.TryFit(marks, out _, out var error));
    Assert.Contains("one line", error);
  }

  [Fact]
  public void RefusesMarksOnDifferentFloors()
  {
    GroundSample[] marks = [.. Corners[..3], Mark("d", -4, 22, floorM: 3)];
    Assert.False(GroundHomography.TryFit(marks, out _, out var error));
    Assert.Contains("different floors", error);
  }
}
