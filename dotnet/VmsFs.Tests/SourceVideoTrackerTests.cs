using VmsFs;

namespace VmsFs.Tests;

// The video state is what the map shows as NO_VIDEO: a stream that stopped delivering frames, as
// derived from the heartbeats. "Never heard" must never become "dead", a media-server restart must
// not flash every camera, and a whole-node silence is the server, not the cameras.
public sealed class SourceVideoTrackerTests
{
  private const string Camera = "cam1";
  private const string Stream = "main";
  private const string SecondStream = "sub";
  private const string FirstRun = "run-1";
  private const string SecondRun = "run-2";

  private static readonly DateTime Start = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
  private static readonly TimeSpan Grace = SourceLivenessTracker.Grace;
  private static readonly TimeSpan Stale = SourceLivenessTracker.Stale;

  private static DateTime At(double seconds) => Start.AddSeconds(seconds);

  private static (string Camera, string Stream) Key(string stream = Stream) =>
    SourceVideoTracker.Key(Camera, stream);

  private static SourceVideoTracker WithLiveStream()
  {
    var tracker = new SourceVideoTracker();
    tracker.Observe(Camera, Stream, alive: true, FirstRun, Start);
    return tracker;
  }

  private static SourceVideoTracker.VideoState StateOf(
    SourceVideoTracker tracker, string stream = Stream, double at = 0)
  {
    var snapshot = tracker.Snapshot([Camera], At(at));
    return snapshot
      .Where(s => s.StreamName == stream)
      .Select(s => s.State)
      .SingleOrDefault(SourceVideoTracker.VideoState.Unknown);
  }

  [Fact]
  public void AReportedFrameMakesTheStreamAlive()
  {
    var tracker = new SourceVideoTracker();

    Assert.True(tracker.Observe(Camera, Stream, alive: true, FirstRun, Start));
    Assert.Equal(SourceVideoTracker.VideoState.Alive, StateOf(tracker));
  }

  [Fact]
  public void AFirstDeadReportStaysUnknownUntilTheGrace()
  {
    var tracker = new SourceVideoTracker();

    // The reporter has only just started: a camera that has not produced a frame yet is not a
    // camera whose video is gone.
    Assert.False(tracker.Observe(Camera, Stream, alive: false, FirstRun, Start));
    Assert.Equal(SourceVideoTracker.VideoState.Unknown, StateOf(tracker));

    Assert.False(tracker.Observe(Camera, Stream, alive: false, FirstRun, At(Grace.TotalSeconds - 1)));
    Assert.Equal(SourceVideoTracker.VideoState.Unknown, StateOf(tracker));

    Assert.True(tracker.Observe(Camera, Stream, alive: false, FirstRun, At(Grace.TotalSeconds)));
    Assert.Equal(SourceVideoTracker.VideoState.NoVideo, StateOf(tracker));
  }

  [Fact]
  public void ALostFrameMakesAnAliveStreamNoVideoImmediately()
  {
    var tracker = WithLiveStream();

    Assert.True(tracker.Observe(Camera, Stream, alive: false, FirstRun, At(5)));
    Assert.Equal(SourceVideoTracker.VideoState.NoVideo, StateOf(tracker, at: 5));
  }

  [Fact]
  public void ARestartedMediaServerStartsWithUnknownAndRecoversFromUnknown()
  {
    var tracker = WithLiveStream();

    // The new run's first report is dead for every stream until frames arrive. That is not video
    // loss, and the grace window keeps it from flashing every camera on the node.
    Assert.False(tracker.Observe(Camera, Stream, alive: false, SecondRun, At(5)));
    Assert.Equal(SourceVideoTracker.VideoState.Unknown, StateOf(tracker, at: 5));

    Assert.True(tracker.Observe(Camera, Stream, alive: true, SecondRun, At(6)));
    Assert.Equal(SourceVideoTracker.VideoState.Alive, StateOf(tracker, at: 6));
  }

  [Fact]
  public void ANewRunThatStartsDeadBecomesNoVideoOnlyAfterTheGrace()
  {
    var tracker = WithLiveStream();
    tracker.Observe(Camera, Stream, alive: false, SecondRun, At(5));

    Assert.False(tracker.Observe(Camera, Stream, alive: false, SecondRun, At(5 + Grace.TotalSeconds - 1)));
    Assert.Equal(SourceVideoTracker.VideoState.Unknown, StateOf(tracker, at: 5));

    Assert.True(tracker.Observe(Camera, Stream, alive: false, SecondRun, At(5 + Grace.TotalSeconds)));
    Assert.Equal(SourceVideoTracker.VideoState.NoVideo, StateOf(tracker, at: 5 + Grace.TotalSeconds));
  }

  [Fact]
  public void AnEmptyInstanceIdDoesNotRestartTheRun()
  {
    var tracker = WithLiveStream();

    // An older producer that omits the instance id must keep the run: the dead report is a real
    // loss, not the first report of a new run.
    Assert.True(tracker.Observe(Camera, Stream, alive: false, instanceId: null, At(1)));
    Assert.Equal(SourceVideoTracker.VideoState.NoVideo, StateOf(tracker, at: 1));
  }

  [Fact]
  public void ASingleSilentStreamIsNoVideo()
  {
    var tracker = WithLiveStream();

    var changed = tracker.Decay(
      new HashSet<(string Camera, string Stream)> { Key() }, wholeNodeSilent: false, At(Stale.TotalSeconds));

    Assert.Equal(Stream, Assert.Single(changed).StreamName);
    Assert.Equal(SourceVideoTracker.VideoState.NoVideo, StateOf(tracker, at: Stale.TotalSeconds));
  }

  [Fact]
  public void AWholeSilentNodeSaysNothingAboutTheCameras()
  {
    var tracker = WithLiveStream();

    var changed = tracker.Decay(
      new HashSet<(string Camera, string Stream)> { Key() }, wholeNodeSilent: true, At(Stale.TotalSeconds));

    Assert.Empty(changed);
    Assert.Equal(SourceVideoTracker.VideoState.Unknown, StateOf(tracker, at: Stale.TotalSeconds));
  }

  [Fact]
  public void ANeverHeardStreamIsNotListed()
  {
    var tracker = new SourceVideoTracker();

    var changed = tracker.Decay(
      new HashSet<(string Camera, string Stream)> { Key() }, wholeNodeSilent: false, At(Stale.TotalSeconds));

    Assert.Empty(changed);
    Assert.Empty(tracker.Snapshot([Camera], At(Stale.TotalSeconds)));
  }

  [Fact]
  public void AStaleEntryReadsAsUnknown()
  {
    var tracker = WithLiveStream();

    // Silence is the sweeper's verdict; until it speaks, a stale "alive" is not a state.
    Assert.Empty(tracker.Snapshot([Camera], At(Stale.TotalSeconds)));
  }

  [Fact]
  public void PruneToForgetsOnlyRemovedStreams()
  {
    var tracker = new SourceVideoTracker();
    tracker.Observe(Camera, Stream, alive: true, FirstRun, Start);
    tracker.Observe(Camera, SecondStream, alive: true, FirstRun, Start);

    tracker.PruneTo(new HashSet<(string Camera, string Stream)> { Key() });

    var snapshot = tracker.Snapshot([Camera], At(1));
    Assert.Equal(Stream, Assert.Single(snapshot).StreamName);
  }

  [Fact]
  public void AnUnchangedReportRaisesNoNotification()
  {
    var tracker = new SourceVideoTracker();

    Assert.True(tracker.Observe(Camera, Stream, alive: true, FirstRun, Start));
    Assert.False(tracker.Observe(Camera, Stream, alive: true, FirstRun, At(1)));

    Assert.True(tracker.Observe(Camera, Stream, alive: false, FirstRun, At(2)));
    Assert.False(tracker.Observe(Camera, Stream, alive: false, FirstRun, At(3)));
  }
}
