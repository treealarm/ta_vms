using VmsMap.Adapter;
using VmsMap.Clients;

namespace VmsMap.Tests;

// The presence table: video says whether frames arrive, ONVIF says whether the camera answers at
// all, and either answer may be unknown. NO_VIDEO needs every configured stream known dead; a
// stream that was never heard is not a dead one. "Could not ask" never changes the connection.
public sealed class CameraPresenceRulesTests
{
  private const string CameraId = "cam1";
  private const string Name = "front door";
  private const string ServerId = "srv1";
  private const string MainStream = "main";
  private const string SubStream = "sub";

  private static CameraInfo Camera(
    bool hasOnvif = true, params string[] streams) =>
    new(CameraId, Name, ServerId, VideoStreams: streams, HasOnvif: hasOnvif);

  private static IReadOnlyDictionary<string, VideoChannelState> States(
    params (string Stream, VideoChannelState State)[] states) =>
    states.ToDictionary(s => s.Stream, s => s.State, StringComparer.Ordinal);

  private static CameraVideoSummary Summary(
    CameraInfo camera, params (string Stream, VideoChannelState State)[] states) =>
    CameraPresenceRules.Summarize(camera, States(states));

  [Fact]
  public void AMainStreamDeadAndASubStreamUnknownIsNotNoVideo()
  {
    var camera = Camera(streams: [MainStream, SubStream]);
    var summary = Summary(camera, (MainStream, VideoChannelState.NoVideo));

    Assert.False(summary.AnyAlive);
    Assert.True(summary.AnyNoVideo);
    Assert.False(summary.AllNoVideo);
    Assert.True(CameraPresenceRules.ShouldProbe(camera, summary));

    // Reachable: the camera is there, its video is partly broken. Not NO_VIDEO — the sub stream
    // was never heard from, and that is not evidence of a dead one.
    Assert.Equal(
      new PresenceDecision(Connected: true, NoVideo: false),
      CameraPresenceRules.Decide(summary, CameraReachability.Reachable));
  }

  [Fact]
  public void AMainStreamDeadAndASubStreamAliveIsVideoPresent()
  {
    var camera = Camera(streams: [MainStream, SubStream]);
    var summary = Summary(camera,
      (MainStream, VideoChannelState.NoVideo), (SubStream, VideoChannelState.Alive));

    Assert.True(summary.AnyAlive);
    Assert.False(CameraPresenceRules.ShouldProbe(camera, summary));
    Assert.Equal(
      new PresenceDecision(Connected: true, NoVideo: false),
      CameraPresenceRules.Decide(summary, CameraReachability.Unknown));
  }

  [Fact]
  public void AllConfiguredStreamsDeadIsNoVideo()
  {
    var camera = Camera(streams: [MainStream, SubStream]);
    var summary = Summary(camera,
      (MainStream, VideoChannelState.NoVideo), (SubStream, VideoChannelState.NoVideo));

    Assert.True(summary.AllNoVideo);
  }

  [Fact]
  public void AReachableCameraWithNoVideoIsConnectedAndNoVideo()
  {
    var summary = Summary(Camera(streams: [MainStream]), (MainStream, VideoChannelState.NoVideo));

    Assert.Equal(
      new PresenceDecision(Connected: true, NoVideo: true),
      CameraPresenceRules.Decide(summary, CameraReachability.Reachable));
  }

  [Fact]
  public void AnUnreachableCameraWithNoVideoIsDisconnectedAndNoVideo()
  {
    var summary = Summary(Camera(streams: [MainStream]), (MainStream, VideoChannelState.NoVideo));

    Assert.Equal(
      new PresenceDecision(Connected: false, NoVideo: true),
      CameraPresenceRules.Decide(summary, CameraReachability.Unreachable));
  }

  [Fact]
  public void ACameraWithoutOnvifAndAllStreamsDeadIsDisconnected()
  {
    var camera = Camera(hasOnvif: false, streams: [MainStream]);
    var summary = Summary(camera, (MainStream, VideoChannelState.NoVideo));

    Assert.False(CameraPresenceRules.ShouldProbe(camera, summary));
    Assert.Equal(
      new PresenceDecision(Connected: false, NoVideo: true),
      CameraPresenceRules.Decide(summary, CameraReachability.NoOnvif));
  }

  [Fact]
  public void ACameraWithoutOnvifAndAnUnknownStreamKeepsItsConnection()
  {
    var camera = Camera(hasOnvif: false, streams: [MainStream, SubStream]);
    var summary = Summary(camera, (MainStream, VideoChannelState.NoVideo));

    // No channel can settle the unknown sub stream: nothing is concluded.
    Assert.Equal(
      new PresenceDecision(Connected: null, NoVideo: null),
      CameraPresenceRules.Decide(summary, CameraReachability.NoOnvif));
  }

  [Fact]
  public void NoProbeIsMadeWhenNothingIsKnownAboutTheStreams()
  {
    var camera = Camera(streams: [MainStream]);
    var summary = Summary(camera);

    Assert.False(CameraPresenceRules.ShouldProbe(camera, summary));
    Assert.Equal(
      new PresenceDecision(Connected: null, NoVideo: null),
      CameraPresenceRules.Decide(summary, CameraReachability.Unknown));
  }

  [Fact]
  public void AProbeThatCouldNotBeMadeLeavesTheConnectionAlone()
  {
    var summary = Summary(Camera(streams: [MainStream]), (MainStream, VideoChannelState.NoVideo));

    // The video verdict still stands; the connection is whatever it was.
    Assert.Equal(
      new PresenceDecision(Connected: null, NoVideo: true),
      CameraPresenceRules.Decide(summary, CameraReachability.Unknown));
  }

  [Fact]
  public void ACameraWithoutStreamsIsConnectedByItsOnvifAnswer()
  {
    var camera = Camera();
    var summary = Summary(camera);

    Assert.True(CameraPresenceRules.ShouldProbe(camera, summary));
    Assert.Equal(
      new PresenceDecision(Connected: true, NoVideo: false),
      CameraPresenceRules.Decide(summary, CameraReachability.Reachable));
    Assert.Equal(
      new PresenceDecision(Connected: false, NoVideo: false),
      CameraPresenceRules.Decide(summary, CameraReachability.Unreachable));
  }

  [Fact]
  public void ACameraWithoutStreamsAndWithoutOnvifIsNotEvaluated()
  {
    var camera = Camera(hasOnvif: false);
    var summary = Summary(camera);

    Assert.False(CameraPresenceRules.ShouldProbe(camera, summary));
    Assert.Equal(
      new PresenceDecision(Connected: null, NoVideo: null),
      CameraPresenceRules.Decide(summary, CameraReachability.NoOnvif));
  }
}
