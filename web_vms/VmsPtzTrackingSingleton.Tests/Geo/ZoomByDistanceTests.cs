using VmsPtzTrackingSingleton.Geo;

namespace VmsPtzTrackingSingleton.Tests.Geo;

public class ZoomByDistanceTests
{
  // The simulator's lens: zoom 0..1 steps the focal length by equal ratios from 60° to 3° wide.
  private const double FovWide = 60, FovTele = 3;

  private static double HFovAt(double zoom)
  {
    double Focal(double fov) => 1 / Math.Tan(fov * Math.PI / 360);
    var focal = Focal(FovWide) * Math.Pow(Focal(FovTele) / Focal(FovWide), zoom);
    return 2 * Math.Atan(1 / focal) * 180 / Math.PI;
  }

  private static double ZoomFor(double hfovDeg)
  {
    double Focal(double fov) => 1 / Math.Tan(fov * Math.PI / 360);
    return Math.Log(Focal(hfovDeg) / Focal(FovWide)) / Math.Log(Focal(FovTele) / Focal(FovWide));
  }

  /// <summary>How wide the picture is at a distance, in metres.</summary>
  private static double FrameWidthM(double zoom, double distanceM) =>
    2 * distanceM * Math.Tan(HFovAt(zoom) * Math.PI / 360);

  [Fact]
  public void InterpolatesInTheLogarithmOfDistance()
  {
    var zoom = ZoomByDistance.TryBuild([new(10, 0.2f), new(40, 0.6f)])!;

    // 20 m is halfway from 10 to 40 by ratio, so halfway in zoom.
    Assert.Equal(0.4f, zoom.Zoom(20), 4);
  }

  [Fact]
  public void KeepsAnObjectTheSameSizeOnALensWithEqualRatioSteps()
  {
    // The operator framed 8 m of ground at the nearest and the farthest landmark.
    var samples = new[] { 12.0, 60.0 }.Select(d => new ZoomSample(d, (float)ZoomFor(2 * Math.Atan(4 / d) * 180 / Math.PI)));
    var zoom = ZoomByDistance.TryBuild(samples)!;

    foreach (var distance in new[] { 15.0, 25.0, 40.0, 55.0 })
      Assert.Equal(8.0, FrameWidthM(zoom.Zoom(distance), distance), 1);
  }

  [Fact]
  public void StaysWithinTheSamplesRatherThanGuessingPastThem()
  {
    var zoom = ZoomByDistance.TryBuild([new(40, 0.6f), new(10, 0.2f), new(25, 0.5f)])!;

    Assert.Equal(0.2f, zoom.Zoom(3));
    Assert.Equal(0.6f, zoom.Zoom(200));
    Assert.Equal(0.2f, zoom.Widest);
    Assert.Equal(0.5f, zoom.Zoom(25), 4);
  }

  [Fact]
  public void OneSampleIsOneZoomAndNoneIsNoZoomAtAll()
  {
    var one = ZoomByDistance.TryBuild([new(20, 0.3f)])!;

    Assert.Equal(0.3f, one.Zoom(5));
    Assert.Equal(0.3f, one.Zoom(80));
    Assert.Null(ZoomByDistance.TryBuild([]));
  }

  [Fact]
  public void TwoLandmarksAtOneDistanceDoNotDivideByZero()
  {
    var zoom = ZoomByDistance.TryBuild([new(10, 0.2f), new(20, 0.4f), new(20, 0.5f)])!;

    Assert.True(float.IsFinite(zoom.Zoom(20)));
    Assert.Equal(0.3f, zoom.Zoom(Math.Sqrt(200)), 4);
  }
}
