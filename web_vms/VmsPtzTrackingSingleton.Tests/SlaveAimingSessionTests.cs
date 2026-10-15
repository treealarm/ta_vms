using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using Vms.Analytics;
using Vms.Cfg;
using VmsPtzTrackingSingleton.Geo;
using VmsPtzTrackingSingleton.Tests.Geo;

namespace VmsPtzTrackingSingleton.Tests;

/// <summary>A slave bound to its master through the map, fed the master's detections.</summary>
public class SlaveAimingSessionTests
{
  private sealed class RecordingSender : IPtzCommandSender
  {
    public ConcurrentQueue<(string Camera, float Pan, float Tilt)> Aims { get; } = new();

    public Task<string?> AimAsync(string cameraId, float pan, float tilt, CancellationToken ct)
    {
      Aims.Enqueue((cameraId, pan, tilt));
      return Task.FromResult<string?>(null);
    }

    public Task<string?> MoveAsync(string cameraId, float pan, float tilt, CancellationToken ct) => Task.FromResult<string?>(null);
    public Task<string?> StopAsync(string cameraId, CancellationToken ct) => Task.FromResult<string?>(null);
    public Task<string?> GotoPresetAsync(string cameraId, string presetToken, CancellationToken ct) => Task.FromResult<string?>(null);
  }

  private static readonly TrackingPolicy Policy =
    new(true, [], new HashSet<string>(), "master");

  private static readonly SlaveBindingModel GeoBinding =
    new("slave", "master", PtzSlaveBindingMethod.PtzSlaveBindingGeo, [], null, null);

  /// <summary>A person whose feet are at (x, y) in the master's picture.</summary>
  private static DetectionSample PersonAt(double x, double y) =>
    new("onvif", 7, DetectionKind.Person, (float)x - 0.02f, (float)y - 0.12f, 0.04f, 0.12f, null, DateTime.UtcNow);

  private static async Task<T> Until<T>(Func<T?> probe) where T : class
  {
    var deadline = DateTime.UtcNow.AddSeconds(5);
    while (DateTime.UtcNow < deadline)
    {
      if (probe() is { } value)
        return value;
      await Task.Delay(20);
    }
    throw new TimeoutException("nothing happened within 5 s");
  }

  [Fact]
  public async Task AimsTheSlaveAtThePersonsFeetThroughTheMap()
  {
    var sender = new RecordingSender();
    var pair = new GeoAimPair(TwoCameraWorld.MasterModel(), TwoCameraWorld.SlaveModel());
    var session = new SlaveAimingSession("slave", sender, NullLogger.Instance, new SlaveAim(GeoBinding, pair, null), Policy);
    using var cts = new CancellationTokenSource();
    var loop = session.RunAsync(cts.Token);

    var (x, y) = TwoCameraWorld.InMaster(-5, 10);
    session.Offer(PersonAt(x, y));
    var aim = await Until(() => sender.Aims.TryPeek(out var a) ? Tuple.Create(a) : null);

    var (pan, tilt) = TwoCameraWorld.SlaveReading(-5, 10);
    Assert.Equal("slave", aim.Item1.Camera);
    // The box is drawn in float, so its foot is a few millionths off the point: still far inside
    // the 1e-3 generic (0.18°) of pan this asks for.
    Assert.Equal(pan, aim.Item1.Pan, 3);
    Assert.Equal(tilt, aim.Item1.Tilt, 3);
    Assert.Equal(string.Empty, session.Snapshot().LastError);

    await cts.CancelAsync();
    await loop;
  }

  [Fact]
  public async Task SaysWhyWhenThePersonIsBeyondTheMastersHorizon()
  {
    var sender = new RecordingSender();
    var pair = new GeoAimPair(TwoCameraWorld.MasterModel(), TwoCameraWorld.SlaveModel());
    var session = new SlaveAimingSession("slave", sender, NullLogger.Instance, new SlaveAim(GeoBinding, pair, null), Policy);
    using var cts = new CancellationTokenSource();
    var loop = session.RunAsync(cts.Token);

    session.Offer(PersonAt(0.5, 0.05));
    var error = await Until(() => session.Snapshot().LastError is { Length: > 0 } e ? e : null);

    Assert.Contains("horizon", error);
    Assert.Empty(sender.Aims);

    await cts.CancelAsync();
    await loop;
  }

  [Fact]
  public async Task SaysWhyWhenTheCalibrationsAreNotThere()
  {
    var sender = new RecordingSender();
    var aim = new SlaveAim(GeoBinding, null, "The camera is not calibrated against landmarks");
    var session = new SlaveAimingSession("slave", sender, NullLogger.Instance, aim, Policy);
    using var cts = new CancellationTokenSource();
    var loop = session.RunAsync(cts.Token);

    session.Offer(PersonAt(0.5, 0.6));
    var error = await Until(() => session.Snapshot().LastError is { Length: > 0 } e ? e : null);

    Assert.Contains("not calibrated", error);
    Assert.Empty(sender.Aims);

    await cts.CancelAsync();
    await loop;
  }
}
