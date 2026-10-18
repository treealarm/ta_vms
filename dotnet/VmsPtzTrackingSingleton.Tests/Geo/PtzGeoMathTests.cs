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

  // How cameras count their axes differs, and ONVIF's generic space says nothing about direction: pan
  // may grow clockwise or anticlockwise, tilt up or down, the units may be -1..1 or degrees, and a
  // camera hung upside down (or set to "reverse") has both axes turned round. Each is a camera the
  // fit must recover without being told which it is.
  public static TheoryData<string> Conventions => ["clockwise", "anticlockwise", "tilt down", "upside down", "degrees", "degrees upside down"];

  private static (float Pan, float Tilt) Reading(string convention, Bearing b)
  {
    double pan = PtzGeoMath.Wrap180(b.AzimuthDeg - 30), tilt = b.ElevationDeg;
    return convention switch
    {
      "clockwise" => ((float)(pan / 180), (float)(tilt / 45 + 0.02)),
      "anticlockwise" => ((float)(-pan / 180), (float)(tilt / 45 + 0.02)),
      "tilt down" => ((float)(pan / 180), (float)(-tilt / 55 - 0.3)),
      "upside down" => ((float)(-pan / 180), (float)(-tilt / 55 - 0.3)),
      "degrees" => ((float)pan, (float)tilt),
      "degrees upside down" => ((float)-pan, (float)(-tilt + 5)),
      _ => throw new ArgumentOutOfRangeException(nameof(convention)),
    };
  }

  [Theory]
  [MemberData(nameof(Conventions))]
  public void RecoversTheCamera_HoweverItCountsItsAxes(string convention)
  {
    GeoObservationSample Seen(string id, double east, double north, double height = 0)
    {
      var b = To(east, north, height);
      var (pan, tilt) = Reading(convention, b);
      return new GeoObservationSample(id, b, pan, tilt);
    }
    var fit = Fit([Seen("a", 40, 60), Seen("b", 80, 20), Seen("c", 15, 25, 3), Seen("d", 60, 70, 1)]);

    Assert.All(fit.Residuals.Values, r => Assert.True(Math.Abs(r!.Pan) < 1e-3 && Math.Abs(r.Tilt) < 1e-3, $"residual {r}"));
    var target = To(45, 35, 1.7);
    var (truePan, trueTilt) = Reading(convention, target);
    var aim = PtzGeoMath.Project(fit, target);
    // To a thousandth of each axis' own unit.
    Assert.Equal(truePan, aim.Pan, 3);
    Assert.Equal(trueTilt, aim.Tilt, 3);
  }

  // An endless head counts each turn from a seam of its own, wherever the installer pointed it: -1
  // and 1 meet there and the readings jump a full turn. The landmarks above lie from 31° to 76°; the
  // seam goes between each pair of neighbours in turn, and behind them, either way round.
  [Theory]
  [InlineData(32, 1)]
  [InlineData(37, 1)]
  [InlineData(60, 1)]
  [InlineData(60, -1)]
  [InlineData(210, 1)]
  [InlineData(210, -1)]
  public void RecoversTheCamera_WhereverItsPanSeamFalls(double seamDeg, int sign)
  {
    float Pan(Bearing b) => (float)(sign * PtzGeoMath.Wrap180(b.AzimuthDeg - seamDeg - 180) / 180);
    GeoObservationSample Seen(string id, double east, double north, double height = 0)
    {
      var b = To(east, north, height);
      return new GeoObservationSample(id, b, Pan(b), TrueTilt(b));
    }
    var fit = Fit([Seen("a", 40, 60), Seen("b", 80, 20), Seen("c", 15, 25, 3), Seen("d", 60, 70, 1)]);

    Assert.All(fit.Residuals.Values, r => Assert.True(Math.Abs(r!.Pan) < 1e-3, $"residual {r}"));
    for (var azimuth = 31.0; azimuth <= 76; azimuth += 1)
    {
      var rad = azimuth * Math.PI / 180;
      var target = To(50 * Math.Sin(rad), 50 * Math.Cos(rad), 1.7);
      var aim = PtzGeoMath.Project(fit, target);
      // The same direction, inside -1..1 even between the true seam and where the fit put it.
      Assert.Equal(0, OneTurn(aim.Pan - Pan(target)), 3);
      Assert.InRange(aim.Pan, -1, 1);
      Assert.False(aim.Extrapolated, $"{azimuth}° is among the landmarks");
    }
  }

  [Fact]
  public void LandmarksAllRoundTheCamera()
  {
    // A head watching every side, with its seam at 150°: no sector, and no one gap behind it.
    static float Pan(Bearing b) => (float)(PtzGeoMath.Wrap180(b.AzimuthDeg - 330) / 180);
    GeoObservationSample At(string id, double azimuthDeg, double distance)
    {
      var rad = azimuthDeg * Math.PI / 180;
      var b = To(distance * Math.Sin(rad), distance * Math.Cos(rad));
      return new GeoObservationSample(id, b, Pan(b), TrueTilt(b));
    }

    var fit = Fit([At("a", 10, 50), At("b", 100, 80), At("c", 190, 30), At("d", 280, 60)]);

    foreach (var azimuth in new[] { 40.0, 140, 160, 230, 320 })
    {
      var rad = azimuth * Math.PI / 180;
      var target = To(40 * Math.Sin(rad), 40 * Math.Cos(rad));
      var aim = PtzGeoMath.Project(fit, target);
      Assert.Equal(0, OneTurn(aim.Pan - Pan(target)), 3);
      Assert.InRange(aim.Pan, -1, 1);
    }
  }

  [Fact]
  public void LeavesAHeadInDegreesUnfolded()
  {
    // Degrees, -180..180, seam among the landmarks: a turn is 360, not 2, and the range is not known.
    static float Pan(Bearing b) => (float)PtzGeoMath.Wrap180(b.AzimuthDeg - 230);
    GeoObservationSample Seen(string id, double east, double north, double height = 0)
    {
      var b = To(east, north, height);
      return new GeoObservationSample(id, b, Pan(b), TrueTilt(b));
    }
    var fit = Fit([Seen("a", 40, 60), Seen("b", 80, 20), Seen("c", 15, 25, 3), Seen("d", 60, 70, 1)]);

    Assert.False(fit.FoldsPan);
    var target = To(25, 40); // 32°, away from the seam at 50°
    Assert.Equal(Pan(target), PtzGeoMath.Project(fit, target).Pan, 3);
  }

  // A pan difference in the generic space folded into ±1: two readings a full turn apart are one direction.
  private static double OneTurn(double pan) => pan - 2 * Math.Round(pan / 2);

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
