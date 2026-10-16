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
    public ConcurrentQueue<(string Camera, float Pan, float Tilt, float Zoom)> Aims { get; } = new();

    public Task<string?> AimAsync(string cameraId, float pan, float tilt, float zoom, CancellationToken ct)
    {
      Aims.Enqueue((cameraId, pan, tilt, zoom));
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
    // No zoom samples: the wide end, as before zoom was aimed at all.
    Assert.Equal(0f, aim.Item1.Zoom);
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

  // Zoom samples at the near and far landmarks of the slave: L4 is about 18 m away, L1 42 m.
  private static readonly Dictionary<string, float> Zooms = new() { ["L4"] = 0.2f, ["L1"] = 0.6f };

  [Fact]
  public async Task ZoomsByHowFarThePersonIsFromTheSlave()
  {
    var sender = new RecordingSender();
    var slave = TwoCameraWorld.SlaveModel(zooms: Zooms);
    var pair = new GeoAimPair(TwoCameraWorld.MasterModel(), slave);
    var session = new SlaveAimingSession("slave", sender, NullLogger.Instance, new SlaveAim(GeoBinding, pair, null), Policy);
    using var cts = new CancellationTokenSource();
    var loop = session.RunAsync(cts.Token);

    var (x, y) = TwoCameraWorld.InMaster(-5, 10);
    session.Offer(PersonAt(x, y));
    var aim = await Until(() => sender.Aims.TryPeek(out var a) ? Tuple.Create(a) : null);

    var expected = slave.Zoom!.Zoom(TwoCameraWorld.SlaveDistance(-5, 10));
    Assert.InRange(expected, 0.2f, 0.6f);
    // The foot is a few millimetres off in float, which moves ln(distance) by nothing worth seeing.
    Assert.Equal(expected, aim.Item1.Zoom, 3);

    await cts.CancelAsync();
    await loop;
  }

  [Fact]
  public async Task FollowsAStepOnTheGroundButNotAShuffle()
  {
    var sender = new RecordingSender();
    var pair = new GeoAimPair(TwoCameraWorld.MasterModel(), TwoCameraWorld.SlaveModel(zooms: Zooms));
    var session = new SlaveAimingSession("slave", sender, NullLogger.Instance, new SlaveAim(GeoBinding, pair, null), Policy);
    using var cts = new CancellationTokenSource();
    var loop = session.RunAsync(cts.Token);

    var (x, y) = TwoCameraWorld.InMaster(-5, 10);
    session.Offer(PersonAt(x, y));
    await Until(() => sender.Aims.TryPeek(out var a) ? Tuple.Create(a) : null);

    // Past the least interval between commands, so only the dead zone can hold the camera back.
    await Task.Delay(TimeSpan.FromSeconds(2.2));
    (x, y) = TwoCameraWorld.InMaster(-5.2, 10.1);
    session.Offer(PersonAt(x, y));
    await Task.Delay(TimeSpan.FromMilliseconds(400));
    Assert.Single(sender.Aims);

    (x, y) = TwoCameraWorld.InMaster(-6, 10);
    session.Offer(PersonAt(x, y));
    await Until(() => sender.Aims.Count == 2 ? sender : null);

    await cts.CancelAsync();
    await loop;
  }

  [Fact]
  public void AimsOutsideTheLandmarksAtTheWidestSampledZoom()
  {
    var slave = TwoCameraWorld.SlaveModel(zooms: Zooms);
    var aim = new SlaveAim(GeoBinding, new GeoAimPair(TwoCameraWorld.MasterModel(), slave), null);

    // North-west of the slave, well round from the landmarks it was calibrated on.
    var (x, y) = TwoCameraWorld.InMaster(12, 30);
    var (position, error) = aim.Position((float)x, (float)y);

    Assert.Null(error);
    Assert.True(position!.Extrapolated);
    Assert.Equal(slave.Zoom!.Widest, position.Zoom);
  }

  [Fact]
  public async Task AHoldPlacedBeforeTheSessionStartedKeepsItsHandsOffTheCamera()
  {
    var sender = new RecordingSender();
    var pair = new GeoAimPair(TwoCameraWorld.MasterModel(), TwoCameraWorld.SlaveModel());
    var hold = new HoldGate();
    hold.Extend(DateTime.UtcNow.AddMinutes(1));
    var session = new SlaveAimingSession("slave", sender, NullLogger.Instance, new SlaveAim(GeoBinding, pair, null), Policy, hold);
    using var cts = new CancellationTokenSource();
    var loop = session.RunAsync(cts.Token);

    var (x, y) = TwoCameraWorld.InMaster(-5, 10);
    session.Offer(PersonAt(x, y));
    await Task.Delay(TimeSpan.FromMilliseconds(400));
    Assert.Empty(sender.Aims);
    Assert.Equal(HoldGate.Reason, session.Snapshot().LastError);

    hold.Release();
    session.Offer(PersonAt(x, y));
    await Until(() => sender.Aims.TryPeek(out var a) ? Tuple.Create(a) : null);

    await cts.CancelAsync();
    await loop;
  }

  [Fact]
  public void AShortHoldDoesNotCutALongerOneShort()
  {
    var hold = new HoldGate();
    var wizard = DateTime.UtcNow.AddSeconds(40);
    hold.Extend(wizard);
    hold.Extend(DateTime.UtcNow.AddSeconds(30));
    Assert.Equal(wizard, hold.Until);

    hold.Release();
    Assert.False(hold.IsHeld);
  }

  [Fact]
  public async Task ARecalibrationReaimsAPersonStandingStill()
  {
    var sender = new RecordingSender();
    var master = TwoCameraWorld.MasterModel();
    var session = new SlaveAimingSession(
      "slave", sender, NullLogger.Instance,
      new SlaveAim(GeoBinding, new GeoAimPair(master, TwoCameraWorld.SlaveModel(zooms: Zooms)), null), Policy);
    using var cts = new CancellationTokenSource();
    var loop = session.RunAsync(cts.Token);

    var (x, y) = TwoCameraWorld.InMaster(-5, 10);
    session.Offer(PersonAt(x, y));
    await Until(() => sender.Aims.TryPeek(out var a) ? Tuple.Create(a) : null);
    await Task.Delay(TimeSpan.FromSeconds(2.2));

    // The same calibrations read again: nothing to re-aim for.
    session.ApplyAim(new SlaveAim(
      GeoBinding, new GeoAimPair(TwoCameraWorld.MasterModel(), TwoCameraWorld.SlaveModel(zooms: Zooms)), null));
    session.Offer(PersonAt(x, y));
    await Task.Delay(TimeSpan.FromMilliseconds(400));
    Assert.Single(sender.Aims);

    // A sample added at L3, as far from the slave as L1 and with its zoom: the calibration has
    // changed but this person's position has not -- the re-aim is the reset's doing alone.
    var redone = new Dictionary<string, float>(Zooms) { ["L3"] = 0.6f };
    session.ApplyAim(new SlaveAim(
      GeoBinding, new GeoAimPair(TwoCameraWorld.MasterModel(), TwoCameraWorld.SlaveModel(zooms: redone)), null));
    session.Offer(PersonAt(x, y));
    await Until(() => sender.Aims.Count == 2 ? sender : null);

    await cts.CancelAsync();
    await loop;
  }

  [Fact]
  public void ASlaveThatDoesNotZoomKeepsItsDeadZoneInTheHeadsUnits()
  {
    var aim = new SlaveAim(GeoBinding, new GeoAimPair(TwoCameraWorld.MasterModel(), TwoCameraWorld.SlaveModel()), null);
    var (x, y) = TwoCameraWorld.InMaster(-5, 10);

    var (position, _) = aim.Position((float)x, (float)y);

    Assert.Null(position!.Ground);
  }

  [Fact]
  public void ToleratesMoreJitterFartherFromTheMaster()
  {
    var aim = new SlaveAim(GeoBinding, new GeoAimPair(TwoCameraWorld.MasterModel(), TwoCameraWorld.SlaveModel(zooms: Zooms)), null);

    var (nx, ny) = TwoCameraWorld.InMaster(0, -5);
    var (fx, fy) = TwoCameraWorld.InMaster(0, 40);
    var near = aim.Position((float)nx, (float)ny).Aim!;
    var far = aim.Position((float)fx, (float)fy).Aim!;

    // 15 m and 60 m from a master 6 m up: the same box jitter covers about sixteen times the ground.
    Assert.InRange(far.GroundToleranceM / near.GroundToleranceM, 8, 30);
    Assert.True(far.GroundToleranceM > 0.5, $"{far.GroundToleranceM:F2} m");
  }
}
