using Microsoft.Extensions.Logging.Abstractions;
using VmsOnvif;
using web_srv.Types.Actors;

namespace VmsOnvif.Tests;

// Presence is a network fact, not a successful ONVIF operation: a camera that answers with 401, a
// SOAP fault or a timeout but accepts a TCP connection is still there. "Could not ask" and "no
// ONVIF configured" are different answers again, and neither may read as an unreachable camera.
public sealed class ReachabilityProbeTests
{
  private const string Camera = "cam1";

  private sealed class FakeConnections : ICameraConnectionSource
  {
    public CameraConnData? Data { get; set; } = Connected();

    public int Calls;

    public Task<CameraConnData?> GetAsync(string cameraId, CancellationToken ct)
    {
      Calls++;
      return Task.FromResult(Data);
    }
  }

  private sealed class FakeReader : IReachabilityReader
  {
    public bool Answers { get; set; } = true;

    public TaskCompletionSource? Gate { get; set; }

    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public int Calls;

    public async Task<bool> AnswersAsync(CameraConnInfo conn, CancellationToken ct)
    {
      Calls++;
      Started.TrySetResult();
      if (Gate is not null)
        await Gate.Task.WaitAsync(ct);
      return Answers;
    }
  }

  private sealed class FakeNetwork : INetworkProbe
  {
    public bool Accepts { get; set; }

    public int Calls;

    public Task<bool> AcceptsConnectionAsync(CameraConnInfo conn, CancellationToken ct)
    {
      Calls++;
      return Task.FromResult(Accepts);
    }
  }

  private sealed class ManualTime : TimeProvider
  {
    public DateTimeOffset Now { get; set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => Now;
  }

  private sealed record Fixture(
    CameraReachabilityProbe Probe,
    FakeConnections Connections,
    FakeReader Reader,
    FakeNetwork Network,
    ManualTime Clock);

  private static Fixture Create()
  {
    var connections = new FakeConnections();
    var reader = new FakeReader();
    var network = new FakeNetwork();
    var clock = new ManualTime();
    var probe = new CameraReachabilityProbe(
      connections, reader, network, clock, NullLogger<CameraReachabilityProbe>.Instance);
    return new Fixture(probe, connections, reader, network, clock);
  }

  private static CameraConnData Connected(CameraConnInfo? conn = null) =>
    new(conn ?? new CameraConnInfo("10.0.0.1", 80, "user", "pass", "", "srv"), []);

  [Fact]
  public async Task ACameraWithoutOnvifIsNoOnvif()
  {
    var f = Create();
    f.Connections.Data = new CameraConnData(null, []);

    Assert.Equal(ReachabilityResult.NoOnvif, await f.Probe.ProbeAsync(Camera, default));
  }

  [Fact]
  public async Task AnUnresolvedCameraIsUnknown()
  {
    var f = Create();
    f.Connections.Data = null;

    Assert.Equal(ReachabilityResult.Unknown, await f.Probe.ProbeAsync(Camera, default));
  }

  [Fact]
  public async Task AnAnsweringOnvifIsReachable()
  {
    var f = Create();

    Assert.Equal(ReachabilityResult.Reachable, await f.Probe.ProbeAsync(Camera, default));
    Assert.Equal(0, f.Network.Calls);
  }

  [Fact]
  public async Task AnOnvifCallThatFailsButAcceptsTcpIsReachable()
  {
    var f = Create();
    f.Reader.Answers = false;
    f.Network.Accepts = true;

    // A 401, a SOAP fault or an unsupported operation all prove a device; only a refused
    // connection says the camera is not there.
    Assert.Equal(ReachabilityResult.Reachable, await f.Probe.ProbeAsync(Camera, default));
    Assert.Equal(1, f.Network.Calls);
  }

  [Fact]
  public async Task AnOnvifCallThatFailsAndRefusesTcpIsUnreachable()
  {
    var f = Create();
    f.Reader.Answers = false;
    f.Network.Accepts = false;

    Assert.Equal(ReachabilityResult.Unreachable, await f.Probe.ProbeAsync(Camera, default));
  }

  [Fact]
  public async Task TheAnswerIsCachedUntilItExpires()
  {
    var f = Create();
    await f.Probe.ProbeAsync(Camera, default);
    Assert.Equal(1, f.Reader.Calls);

    f.Clock.Now = f.Clock.Now.AddSeconds(5);
    await f.Probe.ProbeAsync(Camera, default);
    Assert.Equal(1, f.Reader.Calls);

    f.Clock.Now = f.Clock.Now.AddSeconds(6);
    await f.Probe.ProbeAsync(Camera, default);
    Assert.Equal(2, f.Reader.Calls);
  }

  [Fact]
  public async Task ConcurrentProbesShareOneCall()
  {
    var f = Create();
    f.Reader.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    var first = f.Probe.ProbeAsync(Camera, default);
    var second = f.Probe.ProbeAsync(Camera, default);
    await f.Reader.Started.Task;

    Assert.Equal(1, f.Reader.Calls);

    f.Reader.Gate.SetResult();
    Assert.Equal(ReachabilityResult.Reachable, await first);
    Assert.Equal(ReachabilityResult.Reachable, await second);
  }

  [Fact]
  public async Task CancellationPropagates()
  {
    var f = Create();
    f.Reader.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    using var cts = new CancellationTokenSource();

    var probe = f.Probe.ProbeAsync(Camera, cts.Token);
    await f.Reader.Started.Task;
    cts.Cancel();

    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => probe);
  }
}
