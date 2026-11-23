using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using VmsMap.Adapter;
using VmsMap.Clients;
using VmsMap.Db;
using VmsMap.Domain;

namespace VmsMap.Tests;

// The service that writes presence: video first, ONVIF only where there is a reason, and one
// operation that leaves the other states of the camera alone. The payload never decides anything —
// the video source and the probe do.
public sealed class CameraPresenceServiceTests
{
  private const string CameraId = TestCameras.One;
  private const string Name = "front door";
  private const string ServerId = "srv1";
  private const string MainStream = "main";

  private sealed class FakeVideo : ICameraVideoStateSource
  {
    public Dictionary<string, IReadOnlyDictionary<string, VideoChannelState>> States =
      new(StringComparer.Ordinal);

    public int Calls;

    public Task<IReadOnlyDictionary<string, IReadOnlyDictionary<string, VideoChannelState>>> GetAsync(
      IReadOnlyCollection<CameraInfo> cameras, CancellationToken ct)
    {
      Calls++;
      return Task.FromResult<IReadOnlyDictionary<string, IReadOnlyDictionary<string, VideoChannelState>>>(
        States);
    }
  }

  private sealed class FakeProbe : ICameraReachabilityProbe
  {
    public CameraReachability Result { get; set; } = CameraReachability.Reachable;

    public int Calls;

    public Task<CameraReachability> ProbeAsync(CameraInfo camera, CancellationToken ct)
    {
      Calls++;
      return Task.FromResult(Result);
    }
  }

  private sealed record Fixture(
    VmsMapDbContext Db,
    DeviceNodeService Nodes,
    CameraStateWriter Writer,
    FakeVideo Video,
    FakeProbe Probe,
    CameraPresenceService Presence);

  private static Fixture Create()
  {
    var options = new DbContextOptionsBuilder<VmsMapDbContext>()
      .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
      .Options;

    var db = new VmsMapDbContext(options);
    var map = new MapDb(db);
    var stateService = new StateService(db);
    var statesUpdate = new StatesUpdateService(stateService, map, db);
    var writer = new CameraStateWriter(new ObjectStateWriter(db, stateService, statesUpdate));
    var nodes = TestWiring.DeviceNodes(db, map, new MapUpdateService(
      db,
      map,
      new TracksUpdateService(map, new TrackService(db, new LevelService(db)),
        new GeoService(db, new LevelService(db))),
      statesUpdate));

    var video = new FakeVideo();
    var probe = new FakeProbe();
    var presence = new CameraPresenceService(
      video, probe, writer, NullLogger<CameraPresenceService>.Instance);

    return new Fixture(db, nodes, writer, video, probe, presence);
  }

  private static CameraInfo Camera(
    bool hasOnvif = true, bool disabled = false, params string[] streams) =>
    new(CameraId, Name, ServerId, Disabled: disabled, VideoStreams: streams, HasOnvif: hasOnvif);

  private static void Video(Fixture f, params (string Stream, VideoChannelState State)[] states)
  {
    f.Video.States[CameraId] = states.ToDictionary(
      s => s.Stream, s => s.State, StringComparer.Ordinal);
  }

  private static async Task<string?> State(Fixture f)
  {
    var id = Guid.Parse(CameraObjectId.For(CameraId));
    var row = await f.Db.ObjectStates
      .Include(s => s.states)
      .FirstOrDefaultAsync(s => s.id == id);
    return row is null ? null : string.Join(",", row.states.Select(s => s.state).OrderBy(s => s));
  }

  private static string States(params string[] states) => string.Join(",", states.OrderBy(s => s));

  [Fact]
  public async Task ALiveStreamMakesTheCameraConnectedWithoutNoVideo()
  {
    var f = Create();
    var camera = Camera(streams: [MainStream]);
    await f.Nodes.EnsureCameraAsync(camera, default);
    await f.Writer.ApplyPresenceAsync(CameraId, connected: false, noVideo: true, default);

    Video(f, (MainStream, VideoChannelState.Alive));
    await f.Presence.EvaluateAsync(camera, default);

    Assert.Equal(States(CameraStateWriter.Connected), await State(f));
    Assert.Equal(0, f.Probe.Calls);
  }

  [Fact]
  public async Task AReachableCameraWithoutVideoIsConnectedAndNoVideo()
  {
    var f = Create();
    var camera = Camera(streams: [MainStream]);
    await f.Nodes.EnsureCameraAsync(camera, default);
    Video(f, (MainStream, VideoChannelState.NoVideo));
    f.Probe.Result = CameraReachability.Reachable;

    await f.Presence.EvaluateAsync(camera, default);

    Assert.Equal(
      States(CameraStateWriter.Connected, CameraStateWriter.NoVideo), await State(f));
  }

  [Fact]
  public async Task AnUnreachableCameraWithoutVideoIsDisconnectedAndNoVideo()
  {
    var f = Create();
    var camera = Camera(streams: [MainStream]);
    await f.Nodes.EnsureCameraAsync(camera, default);
    Video(f, (MainStream, VideoChannelState.NoVideo));
    f.Probe.Result = CameraReachability.Unreachable;

    await f.Presence.EvaluateAsync(camera, default);

    Assert.Equal(
      States(CameraStateWriter.Disconnected, CameraStateWriter.NoVideo), await State(f));
  }

  [Fact]
  public async Task ACameraWithoutOnvifFallsBackToItsVideo()
  {
    var f = Create();
    var camera = Camera(hasOnvif: false, streams: [MainStream]);
    await f.Nodes.EnsureCameraAsync(camera, default);
    Video(f, (MainStream, VideoChannelState.NoVideo));

    await f.Presence.EvaluateAsync(camera, default);

    Assert.Equal(0, f.Probe.Calls);
    Assert.Equal(
      States(CameraStateWriter.Disconnected, CameraStateWriter.NoVideo), await State(f));
  }

  [Fact]
  public async Task AProbeThatCouldNotBeMadeLeavesTheConnectionAlone()
  {
    var f = Create();
    var camera = Camera(streams: [MainStream]);
    await f.Nodes.EnsureCameraAsync(camera, default);
    await f.Writer.ApplyPresenceAsync(CameraId, connected: true, noVideo: null, default);

    Video(f, (MainStream, VideoChannelState.NoVideo));
    f.Probe.Result = CameraReachability.Unknown;

    await f.Presence.EvaluateAsync(camera, default);

    Assert.Equal(
      States(CameraStateWriter.Connected, CameraStateWriter.NoVideo), await State(f));
  }

  [Fact]
  public async Task ARecoveredStreamClearsNoVideo()
  {
    var f = Create();
    var camera = Camera(streams: [MainStream]);
    await f.Nodes.EnsureCameraAsync(camera, default);
    await f.Writer.ApplyPresenceAsync(CameraId, connected: false, noVideo: true, default);

    Video(f, (MainStream, VideoChannelState.Alive));
    await f.Presence.EvaluateAsync(camera, default);

    Assert.Equal(States(CameraStateWriter.Connected), await State(f));
  }

  [Fact]
  public async Task ACameraAbsentFromTheAnswerKeepsItsStates()
  {
    var f = Create();
    var camera = Camera(streams: [MainStream]);
    await f.Nodes.EnsureCameraAsync(camera, default);
    await f.Writer.ApplyPresenceAsync(CameraId, connected: true, noVideo: false, default);

    // Nothing was heard about the streams: the video source has no entry at all.
    await f.Presence.EvaluateAsync(camera, default);

    Assert.Equal(States(CameraStateWriter.Connected), await State(f));
    Assert.Equal(0, f.Probe.Calls);
  }

  [Fact]
  public async Task ACameraWithoutStreamsIsConnectedByItsOnvifAnswer()
  {
    var f = Create();
    var camera = Camera();
    await f.Nodes.EnsureCameraAsync(camera, default);
    f.Probe.Result = CameraReachability.Reachable;

    await f.Presence.EvaluateAsync(camera, default);

    Assert.Equal(1, f.Probe.Calls);
    Assert.Equal(States(CameraStateWriter.Connected), await State(f));
  }

  [Fact]
  public async Task ACameraWithoutStreamsAndWithoutOnvifIsNotEvaluated()
  {
    var f = Create();
    var camera = Camera(hasOnvif: false);
    await f.Nodes.EnsureCameraAsync(camera, default);

    await f.Presence.EvaluateAsync(camera, default);

    Assert.Equal(0, f.Probe.Calls);
    Assert.Null(await State(f));
  }

  [Fact]
  public async Task ADisabledCameraOnlyClearsNoVideo()
  {
    var f = Create();
    var camera = Camera(disabled: true, streams: [MainStream]);
    await f.Nodes.EnsureCameraAsync(camera, default);
    await f.Writer.ApplyPresenceAsync(CameraId, connected: true, noVideo: true, default);

    Video(f, (MainStream, VideoChannelState.NoVideo));
    await f.Presence.EvaluateAsync(camera, default);

    Assert.Equal(States(CameraStateWriter.Connected), await State(f));
    Assert.Equal(0, f.Probe.Calls);
  }
}
