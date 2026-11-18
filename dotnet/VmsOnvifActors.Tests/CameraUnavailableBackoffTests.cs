namespace VmsOnvifActors.Tests;

/// The window a camera enters when its ONVIF services cannot be resolved. Its point is the number
/// of discoveries per unit of time, not the shape of the answer: a caller polling once a second
/// and a caller polling once an hour must both cost the same while the camera is off.
public class CameraUnavailableBackoffTests
{
  private const string Camera = "camera-1";
  private const string OtherCamera = "camera-2";
  private static readonly TimeSpan Window = TimeSpan.FromSeconds(5);
  private static readonly DateTimeOffset Start = new(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);

  [Fact]
  public void ACameraIsBackedOffOnlyUntilItsWindowEnds()
  {
    var clock = new ManualTime(Start);
    var backoff = new CameraUnavailableBackoff(Window, clock);

    Assert.False(backoff.IsBackedOff(Camera));

    backoff.Note(Camera);

    Assert.True(backoff.IsBackedOff(Camera));
    clock.Now = Start + Window - TimeSpan.FromMilliseconds(1);
    Assert.True(backoff.IsBackedOff(Camera));

    clock.Now = Start + Window;
    Assert.False(backoff.IsBackedOff(Camera));
  }

  [Fact]
  public void AnotherCameraIsNotAffectedByTheWindow()
  {
    var clock = new ManualTime(Start);
    var backoff = new CameraUnavailableBackoff(Window, clock);

    backoff.Note(Camera);

    Assert.False(backoff.IsBackedOff(OtherCamera));
  }

  [Fact]
  public void NotingAgainRestartsTheWindowFromThatMoment()
  {
    var clock = new ManualTime(Start);
    var backoff = new CameraUnavailableBackoff(Window, clock);

    backoff.Note(Camera);

    // One second before the first window would end, the camera is noted again.
    var secondNoteAt = Start + Window - TimeSpan.FromSeconds(1);
    clock.Now = secondNoteAt;
    backoff.Note(Camera);

    clock.Now = Start + Window + TimeSpan.FromSeconds(1);
    Assert.True(backoff.IsBackedOff(Camera));

    clock.Now = secondNoteAt + Window - TimeSpan.FromMilliseconds(1);
    Assert.True(backoff.IsBackedOff(Camera));

    clock.Now = secondNoteAt + Window;
    Assert.False(backoff.IsBackedOff(Camera));
  }

  private sealed class ManualTime(DateTimeOffset start) : TimeProvider
  {
    public DateTimeOffset Now { get; set; } = start;

    public override DateTimeOffset GetUtcNow() => Now;
  }
}
