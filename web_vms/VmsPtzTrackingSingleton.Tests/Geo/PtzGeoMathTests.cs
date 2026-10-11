using VmsPtzTrackingSingleton.Geo;

namespace VmsPtzTrackingSingleton.Tests.Geo;

/// <summary>
/// The landmark calibration decides where a camera turns when someone clicks the map, so it is
/// checked against a synthetic camera whose true behaviour the test knows.
/// </summary>
public class PtzGeoMathTests
{
  private const double CameraHeight = 6;
  private static readonly Enu Camera = new(0, 0);

  // A camera whose pan zero faces 30° and whose -1..1 spans the full turn, with tilt -1..1 over
  // ±45° and a small mounting offset — the kind of numbers an ONVIF camera reports.
  private static float TruePan(Bearing b) => (float)(PtzGeoMath.Wrap180(b.AzimuthDeg - 30) / 180);
  private static float TrueTilt(Bearing b) => (float)(b.ElevationDeg / 45 + 0.02);

  private static Bearing To(double east, double north, double height = 0) =>
    PtzGeoMath.BearingTo(Camera, CameraHeight, new Enu(east, north), height);

  private static GeoObservationSample Observe(string id, double east, double north, double height = 0)
  {
    var b = To(east, north, height);
    return new GeoObservationSample(id, b, TruePan(b), TrueTilt(b));
  }

  private static PtzGeoFit Fit(IReadOnlyList<GeoObservationSample> observations)
  {
    Assert.True(PtzGeoMath.TryFit(observations, out var fit, out var error), error ?? "fit failed");
    return fit!;
  }

  [Fact]
  public void MeasuresBearingFromTheCamera()
  {
    var north = To(0, 100);
    Assert.Equal(0, north.AzimuthDeg, 6);
    Assert.Equal(-Math.Atan(6.0 / 100) * 180 / Math.PI, north.ElevationDeg, 6);
    Assert.Equal(Math.Sqrt(100 * 100 + 6 * 6), north.DistanceM, 6);

    Assert.Equal(90, To(100, 0).AzimuthDeg, 6);
    Assert.Equal(270, To(-100, 0).AzimuthDeg, 6);
    Assert.True(To(0, 100, 10).ElevationDeg > 0);
  }

  [Fact]
  public void RecoversTheCameraAndAimsBetweenLandmarks()
  {
    var fit = Fit([Observe("a", 40, 60), Observe("b", 80, 20), Observe("c", 15, 25, 3)]);

    var target = To(45, 35, 1.7);
    var aim = PtzGeoMath.Project(fit, target);

    Assert.Equal(TruePan(target), aim.Pan, 4);
    Assert.Equal(TrueTilt(target), aim.Tilt, 4);
    Assert.False(aim.Extrapolated);
  }

  [Fact]
  public void TwoLandmarksAreEnoughButCannotBeChecked()
  {
    var fit = Fit([Observe("a", 40, 60), Observe("b", 70, 5, 2)]);

    Assert.All(fit.Residuals.Values, r => Assert.Null(r));
  }

  [Fact]
  public void LeaveOneOutPointsAtTheSlip()
  {
    var observations = new List<GeoObservationSample>
    {
      Observe("a", 40, 60), Observe("b", 80, 20), Observe("c", 15, 25, 3), Observe("d", 60, 70, 1),
    };
    var good = observations[1];
    observations[1] = good with { Pan = good.Pan + 0.3f };

    var fit = Fit(observations);
    var worst = fit.Residuals.MaxBy(r => r.Value!.Pan).Key;

    Assert.Equal("b", worst);
  }

  [Fact]
  public void LandmarksEitherSideOfNorthAreOneSector()
  {
    // A camera facing north with landmarks at 340°, 355° and 15°: unwrapped wrongly, 355° and 15°
    // would be 340° apart and the slope would come out backwards.
    static float Pan(Bearing b) => (float)(PtzGeoMath.Wrap180(b.AzimuthDeg) / 180);
    GeoObservationSample At(string id, double azimuthDeg, double distance)
    {
      var rad = azimuthDeg * Math.PI / 180;
      var b = To(distance * Math.Sin(rad), distance * Math.Cos(rad));
      return new GeoObservationSample(id, b, Pan(b), TrueTilt(b));
    }

    var fit = Fit([At("a", 340, 50), At("b", 355, 80), At("c", 15, 30)]);
    var target = To(0, 60);
    var aim = PtzGeoMath.Project(fit, target);

    Assert.Equal(Pan(target), aim.Pan, 4);
    Assert.False(aim.Extrapolated);
  }

  [Fact]
  public void FlagsADirectionOutsideTheLandmarks()
  {
    var fit = Fit([Observe("a", 40, 60), Observe("b", 80, 20), Observe("c", 15, 25, 3)]);

    var behind = PtzGeoMath.Project(fit, To(-50, -50));

    Assert.True(behind.Extrapolated);
  }

  [Fact]
  public void RefusesACameraThatReportsOnePosition()
  {
    var observations = new[] { Observe("a", 40, 60), Observe("b", 80, 20), Observe("c", 15, 25, 3) }
      .Select(o => o with { Pan = 0.25f })
      .ToList();

    Assert.False(PtzGeoMath.TryFit(observations, out _, out var error));
    Assert.Contains("pan", error);
  }

  [Fact]
  public void RefusesLandmarksInOneDirection()
  {
    Assert.False(PtzGeoMath.TryFit([Observe("a", 10, 50), Observe("b", 20, 100)], out _, out var error));
    Assert.Contains("direction", error);
  }

  [Fact]
  public void RefusesASingleLandmark()
  {
    Assert.False(PtzGeoMath.TryFit([Observe("a", 10, 50)], out _, out _));
  }
}
