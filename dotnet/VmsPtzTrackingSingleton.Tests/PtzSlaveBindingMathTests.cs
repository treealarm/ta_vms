using VmsPtzTrackingSingleton;
using Vms.Analytics;

namespace VmsPtzTrackingSingleton.Tests;

/// <summary>
/// The binding is the one part of master/slave aiming that can be proven without a camera, and it
/// is the part that decides whether the slave arrives at the object or somewhere near it.
/// </summary>
public class PtzSlaveBindingMathTests
{
  private static BindingPoint At(float x, float y, float pan, float tilt) =>
    new(x, y, pan, tilt, null, null);

  // A survey covering the frame's perimeter, mapped by an arbitrary map the test can check against.
  private static List<BindingPoint> Survey(Func<float, float, (float Pan, float Tilt)> map) =>
    new[] { (0.1f, 0.2f), (0.9f, 0.15f), (0.85f, 0.9f), (0.15f, 0.8f), (0.5f, 0.5f), (0.5f, 0.95f) }
      .Select(p =>
      {
        var (pan, tilt) = map(p.Item1, p.Item2);
        return At(p.Item1, p.Item2, pan, tilt);
      })
      .ToList();

  private static BindingFit Fit(IReadOnlyList<BindingPoint> points, double lambda = 0)
  {
    Assert.True(
      PtzSlaveBindingMath.TryFit(points, lambda, out var fit, out var error),
      error ?? "fit failed");
    return fit!;
  }

  [Fact]
  public void RecoversAnAffineMapExactly()
  {
    // Any affine map is in the spline's null space, so it must come back with no bending at all.
    (float, float) Map(float x, float y) => ((0.8f * x) - (0.3f * y) + 0.1f, (0.2f * x) + (1.4f * y) - 0.5f);
    var fit = Fit(Survey(Map));

    foreach (var (x, y) in new[] { (0.3f, 0.4f), (0.7f, 0.25f), (0.45f, 0.7f) })
    {
      var expected = Map(x, y);
      var aim = PtzSlaveBindingMath.Project(fit, x, y);
      Assert.Equal(expected.Item1, aim.Pan, 3);
      Assert.Equal(expected.Item2, aim.Tilt, 3);
    }
  }

  [Fact]
  public void PassesThroughEveryMarkedPoint()
  {
    // What the operator is promised: a place they marked is a place the camera goes.
    (float, float) Map(float x, float y) => (MathF.Sin(3 * x) + y, (x * y) - MathF.Cos(2 * y));
    var points = Survey(Map);
    var fit = Fit(points);

    foreach (var p in points)
    {
      var aim = PtzSlaveBindingMath.Project(fit, p.MasterX, p.MasterY);
      Assert.Equal(p.Pan!.Value, aim.Pan, 4);
      Assert.Equal(p.Tilt!.Value, aim.Tilt, 4);
    }

  }

  [Theory]
  // The whole reason a spline was chosen over a geometric model: a camera hung at an angle, with
  // an axis inverted or the two swapped, is nothing but different numbers in the measurements.
  [InlineData(-1f, 1f, false)]
  [InlineData(1f, -1f, false)]
  [InlineData(-1f, -1f, false)]
  [InlineData(1f, 1f, true)]
  public void HandlesInvertedAndSwappedAxes(float panSign, float tiltSign, bool swap)
  {
    (float, float) Map(float x, float y)
    {
      var (a, b) = swap ? (y, x) : (x, y);
      return (panSign * (a - 0.5f) * 2f, tiltSign * (b - 0.5f) * 2f);
    }

    var fit = Fit(Survey(Map));
    var expected = Map(0.35f, 0.65f);
    var aim = PtzSlaveBindingMath.Project(fit, 0.35f, 0.65f);
    Assert.Equal(expected.Item1, aim.Pan, 3);
    Assert.Equal(expected.Item2, aim.Tilt, 3);
  }

  [Fact]
  public void HandlesANonLinearTilt()
  {
    // A camera whose tilt is not proportional to what it sees -- the case a homography would fight.
    (float, float) Map(float x, float y) => (x - 0.5f, -1f / (y + 0.2f));
    var points = Survey(Map).Concat(new[]
    {
      At(0.3f, 0.35f, 0.3f - 0.5f, -1f / 0.55f),
      At(0.7f, 0.6f, 0.7f - 0.5f, -1f / 0.8f),
    }).ToList();

    var fit = Fit(points);
    var (expectedPan, expectedTilt) = Map(0.45f, 0.55f);
    var aim = PtzSlaveBindingMath.Project(fit, 0.45f, 0.55f);
    Assert.Equal(expectedPan, aim.Pan, 2);
    Assert.Equal(expectedTilt, aim.Tilt, 1);
  }

  [Fact]
  public void RejectsCollinearSurveys()
  {
    var line = Enumerable.Range(0, 5)
      .Select(i => At(0.1f + (0.2f * i), 0.1f + (0.2f * i), 0.1f * i, 0.2f * i))
      .ToList();

    Assert.False(PtzSlaveBindingMath.TryFit(line, 0, out _, out var error));
    Assert.Contains("one line", error);
  }

  [Fact]
  public void RejectsTooFewPointsAndCountsOnlyThoseWithAPosition()
  {
    var two = new List<BindingPoint> { At(0.1f, 0.1f, 0, 0), At(0.9f, 0.9f, 1, 1) };
    Assert.False(PtzSlaveBindingMath.TryFit(two, 0, out _, out var error));
    Assert.Contains("At least 3", error);

    // A camera whose GetStatus answers nothing leaves points with a preset and no coordinates.
    // Those cannot hold up the absolute method, and saying "got 1" is what tells the operator why.
    var mostlyPresets = new List<BindingPoint>
    {
      At(0.1f, 0.1f, 0, 0),
      new(0.9f, 0.9f, null, null, "1", "gate"),
      new(0.5f, 0.2f, null, null, "2", "yard"),
    };
    Assert.False(PtzSlaveBindingMath.TryFit(mostlyPresets, 0, out _, out var tooFew));
    Assert.Contains("got 1", tooFew);
  }

  [Fact]
  public void RejectsPointsMarkedTwiceInTheSameSpot()
  {
    var duplicated = new List<BindingPoint>
    {
      At(0.2f, 0.2f, 0f, 0f),
      At(0.2f, 0.2f, 0.5f, 0.5f),
      At(0.8f, 0.3f, 1f, 0f),
      At(0.5f, 0.9f, 0.5f, 1f),
    };
    Assert.False(PtzSlaveBindingMath.TryFit(duplicated, 0, out _, out var error));
    Assert.Contains("same spot", error);
  }

  [Fact]
  public void FlagsPointsOutsideTheMarkedArea()
  {
    var fit = Fit(Survey((x, y) => (x, y)));

    Assert.False(PtzSlaveBindingMath.Project(fit, 0.5f, 0.5f).Extrapolated);
    Assert.False(PtzSlaveBindingMath.Project(fit, 0.3f, 0.4f).Extrapolated);
    // Beyond every marked point, where the surface continues the measurements rather than
    // interpolating between them.
    Assert.True(PtzSlaveBindingMath.Project(fit, 0.02f, 0.02f).Extrapolated);
    Assert.True(PtzSlaveBindingMath.Project(fit, 0.99f, 0.99f).Extrapolated);
  }

  [Fact]
  public void ResidualsSingleOutAMisMarkedPoint()
  {
    var points = Survey((x, y) => (x, y));
    // One point marked wrong: the operator's hand slipped by a third of the pan range.
    points[2] = points[2] with { Pan = points[2].Pan + 0.3f };

    var fit = Fit(points, PtzSlaveBindingMath.DefaultLambda);

    var worst = fit.Residuals.Select((r, i) => (Residual: r ?? 0f, Index: i)).MaxBy(t => t.Residual);
    Assert.Equal(2, worst.Index);
    // At the size of the slip, not at the size of the arithmetic: this is what makes the number
    // worth putting in front of an operator.
    Assert.True(worst.Residual > 0.25f, $"the bad point only missed by {worst.Residual}");

    // One bad point raises everyone's number, because it is still in the survey the others are
    // checked against -- so the operator re-checks the largest and refits, rather than reading the
    // list as a verdict on each point independently. Taking it out settles the rest.
    var others = fit.Residuals.Where((_, i) => i != 2).Max() ?? 0f;
    Assert.True(others > 0.1f, "a bad point is expected to unsettle its neighbours");

    var cleaned = Fit(points.Where((_, i) => i != 2).ToList(), PtzSlaveBindingMath.DefaultLambda);
    Assert.All(cleaned.Residuals, r => Assert.True(r < 0.01f, $"still unsettled at {r}"));
  }

  [Fact]
  public void AConsistentSurveyAgreesWithItself()
  {
    var fit = Fit(Survey((x, y) => ((0.8f * x) - (0.3f * y), (0.2f * x) + (1.4f * y))), PtzSlaveBindingMath.DefaultLambda);
    Assert.All(fit.Residuals, r => Assert.True(r < 0.01f, $"residual {r} on a survey with no error in it"));
  }

  [Fact]
  public void ThreePointsCannotCrossCheckEachOther()
  {
    // Three points fix an affine map exactly; drop one and the other two say nothing. Reporting a
    // residual of zero here would read as "all three are good", which is not something the survey
    // knows.
    var minimal = new List<BindingPoint>
    {
      At(0.1f, 0.1f, 0f, 0f), At(0.9f, 0.2f, 1f, 0f), At(0.5f, 0.9f, 0.5f, 1f),
    };
    var fit = Fit(minimal);
    Assert.All(fit.Residuals, Assert.Null);
  }

  // Two zones split at x = 0.5. A footprint is given as (left, right) on the ground line.
  private static readonly List<BindingPoint> TwoZones =
  [
    new(0.25f, 0.5f, null, null, "left", "left"),
    new(0.75f, 0.5f, null, null, "right", "right"),
  ];

  private static string? Zone(float left, float right, string? current) =>
    PtzSlaveBindingMath.StickyZone(
      TwoZones, left, right, 0.5f, current, PtzSlaveBindingMath.ZoneHoldShare)?.PresetToken;

  [Fact]
  public void AnObjectStraddlingABoundaryStaysWhereTheCameraAlreadyIs()
  {
    // A person on the line, their box breathing a couple of percent between frames. Judged by the
    // centre point alone every breath is a different zone and a slew each way.
    var current = "left";
    foreach (var (l, r) in new[] { (0.44f, 0.56f), (0.46f, 0.58f), (0.42f, 0.54f), (0.45f, 0.57f) })
    {
      current = Zone(l, r, current);
      Assert.Equal("left", current);
    }

    // And it is not a bias towards one side: held from the right, the same jitter keeps it there.
    current = "right";
    foreach (var (l, r) in new[] { (0.44f, 0.56f), (0.46f, 0.58f), (0.42f, 0.54f) })
    {
      current = Zone(l, r, current);
      Assert.Equal("right", current);
    }
  }

  [Fact]
  public void ADecisiveMajorityMovesTheCamera()
  {
    // Roughly four fifths of the footprint past the line: the object has gone somewhere.
    Assert.Equal("right", Zone(0.48f, 0.62f, "left"));
    Assert.Equal("left", Zone(0.38f, 0.52f, "right"));
  }

  [Fact]
  public void AMerelyUnequalSplitDoesNot()
  {
    // Two thirds across is not decisive — that is a person turning, not a person leaving.
    Assert.Equal("left", Zone(0.46f, 0.58f, "left"));
    Assert.Equal("right", Zone(0.42f, 0.54f, "right"));
  }

  [Fact]
  public void WithNothingHeldItSimplyTakesTheLargestShare()
  {
    Assert.Equal("left", Zone(0.10f, 0.30f, null));
    Assert.Equal("right", Zone(0.70f, 0.90f, null));
    Assert.Equal("right", Zone(0.48f, 0.62f, null));
  }

  [Fact]
  public void ItFallsBackWhenTheHeldZoneIsGone()
  {
    // The preset the camera sits on was deleted from the camera, so there is nothing to be loyal
    // to and the plain answer is the right one.
    Assert.Equal("left", Zone(0.20f, 0.30f, "deleted"));
  }

  [Fact]
  public void AZeroWidthFootprintStillResolves()
  {
    // A degenerate box must not divide by nothing; it is simply a point.
    Assert.Equal("right", Zone(0.8f, 0.8f, null));
  }

  [Fact]
  public void AnObjectCoveringThreeZonesFollowsWhereMostOfItIs()
  {
    List<BindingPoint> three =
    [
      new(0.15f, 0.5f, null, null, "a", "a"),
      new(0.50f, 0.5f, null, null, "b", "b"),
      new(0.85f, 0.5f, null, null, "c", "c"),
    ];
    string? At(float l, float r, string? current) =>
      PtzSlaveBindingMath.StickyZone(three, l, r, 0.5f, current, PtzSlaveBindingMath.ZoneHoldShare)?.PresetToken;

    // A vehicle spanning all three: the middle holds most of it, and it is not abandoned while it
    // still does.
    Assert.Equal("b", At(0.1f, 0.9f, "b"));
    // Slid decisively into the last zone, with only a sliver left behind.
    Assert.Equal("c", At(0.68f, 0.98f, "b"));
  }

  [Fact]
  public void NearestPicksThePointForThePresetsMethod()
  {
    var points = new List<BindingPoint>
    {
      new(0.1f, 0.1f, null, null, "1", "gate"),
      new(0.9f, 0.2f, null, null, "2", "yard"),
      new(0.5f, 0.9f, null, null, "3", "far end"),
    };

    Assert.Equal("gate", PtzSlaveBindingMath.Nearest(points, 0.2f, 0.15f)!.PresetName);
    Assert.Equal("yard", PtzSlaveBindingMath.Nearest(points, 0.8f, 0.3f)!.PresetName);
    Assert.Equal("far end", PtzSlaveBindingMath.Nearest(points, 0.55f, 0.7f)!.PresetName);
    Assert.Null(PtzSlaveBindingMath.Nearest([], 0.5f, 0.5f));
  }
}

/// <summary>
/// Which box a slave may take its ground point from. The rule that stopped the camera swinging
/// between a person's chin and their feet.
/// </summary>
public class TargetChooserGroundTests
{
  private static DetectionSample Box(DetectionKind kind, long trackId, float top, float height) =>
    new("cv", trackId, kind, 0.4f, top, 0.1f, height, null, DateTime.UtcNow);

  [Fact]
  public void ABodyIsItsOwnGroundContact()
  {
    var person = Box(DetectionKind.Person, 7, 0.3f, 0.5f);
    Assert.Same(person, TargetChooser.GroundContact(person, [person]));
  }

  [Fact]
  public void AFaceIsTradedForTheBodyWithTheSameTrackId()
  {
    // The detector numbers a person and the face found on them alike, differing only by kind.
    var face = Box(DetectionKind.Face, 7, 0.30f, 0.08f);
    var person = Box(DetectionKind.Person, 7, 0.30f, 0.50f);
    var other = Box(DetectionKind.Person, 9, 0.10f, 0.40f);

    var contact = TargetChooser.GroundContact(face, [face, other, person]);

    Assert.Same(person, contact);
    // The difference this makes is the whole point: a chin at 0.38 against feet at 0.80 is nearly
    // half the frame, which is why alternating between them swung the camera between zones.
    Assert.Equal(0.38f, face.Top + face.Height, 3);
    Assert.Equal(0.80f, contact!.Top + contact.Height, 3);
  }

  [Fact]
  public void AFaceWithNoBodyInSightGivesNothing()
  {
    // Guessing feet from a chin means guessing the person's height, and the survey would be fed a
    // point that means nothing. Aiming at nowhere is the honest answer.
    var face = Box(DetectionKind.Face, 7, 0.3f, 0.08f);
    var strangersBody = Box(DetectionKind.Person, 8, 0.3f, 0.5f);

    Assert.Null(TargetChooser.GroundContact(face, [face, strangersBody]));
  }

  [Theory]
  [InlineData(DetectionKind.Person, true)]
  [InlineData(DetectionKind.Vehicle, true)]
  [InlineData(DetectionKind.Animal, true)]
  [InlineData(DetectionKind.Face, false)]
  [InlineData(DetectionKind.LicensePlate, false)]
  public void OnlyWholeObjectsStandOnTheGround(DetectionKind kind, bool expected) =>
    Assert.Equal(expected, TargetChooser.StandsOnGround(kind));
}
