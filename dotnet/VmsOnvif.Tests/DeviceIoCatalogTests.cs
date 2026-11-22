using Microsoft.Extensions.Logging.Abstractions;
using OnvifLib;
using VmsOnvif;

namespace VmsOnvif.Tests;

// The catalogue is the policy "the list is read once and served from the presence afterwards": a
// hit never touches the camera, a miss reads it once and fills the cache, concurrent misses share
// that read, and a settings write is in the cache before the panel's reload looks at it.
public class DeviceIoCatalogTests
{
  private const string CameraId = "cam1";
  private const string Relay1 = "relay1";

  private static DeviceIoCatalog Catalog(FakePresence presence, FakeReader reader) =>
    new(presence, reader, NullLogger<DeviceIoCatalog>.Instance);

  private static DeviceDescriptor Relay(string token, string idle = "closed", string mode = "bistable", int delayMs = 0) =>
    new(token, idle, mode, delayMs);

  [Fact]
  public async Task ARelayListAlreadyInTheCacheIsServedWithoutReadingTheCamera()
  {
    var presence = new FakePresence();
    presence.Devices[(CameraId, DeviceIoKind.Relay)] = [Relay(Relay1)];
    var reader = new FakeReader();
    var catalog = Catalog(presence, reader);

    var relays = await catalog.RelaysAsync(CameraId, default);

    Assert.Equal(Relay1, Assert.Single(relays).Token);
    Assert.Equal(0, reader.RelayReads);
  }

  // The relay's mode and delay are the reason the descriptor was widened; a first read that loses
  // them would show every relay as monostable with no delay, and nothing would look broken.
  [Fact]
  public async Task TheFirstReadOfACameraFillsTheCacheWithTheConfiguredModeAndDelay()
  {
    var presence = new FakePresence();
    var reader = new FakeReader
    {
      Relays = [new OnvifRelayOutput(Relay1, "monostable", "open", 2000)],
    };
    var catalog = Catalog(presence, reader);

    var relays = await catalog.RelaysAsync(CameraId, default);

    Assert.Equal(Relay(Relay1, "open", "monostable", 2000), Assert.Single(relays));
    Assert.Equal(1, reader.RelayReads);

    // The cache answers from now on.
    await catalog.RelaysAsync(CameraId, default);
    Assert.Equal(1, reader.RelayReads);
  }

  [Fact]
  public async Task AnUnreadListIsReadEvenWhileTheOtherListIsCached()
  {
    var presence = new FakePresence();
    presence.Devices[(CameraId, DeviceIoKind.DigitalInput)] = [new DeviceDescriptor("input1", "open")];
    var reader = new FakeReader
    {
      Relays = [new OnvifRelayOutput(Relay1, "bistable", "closed", 0)],
    };
    var catalog = Catalog(presence, reader);

    var inputs = await catalog.InputsAsync(CameraId, default);
    var relays = await catalog.RelaysAsync(CameraId, default);

    // A cached input list is not a read relay list: the miss is read, and the hit is not.
    Assert.Single(inputs);
    Assert.Equal(Relay1, Assert.Single(relays).Token);
    Assert.Equal(1, reader.RelayReads);
    Assert.Equal(0, reader.InputReads);
  }

  [Fact]
  public async Task AColdReadThatFailsCachesNothingAndIsRetried()
  {
    var presence = new FakePresence();
    var reader = new FakeReader { Failure = new TimeoutException("no answer") };
    var catalog = Catalog(presence, reader);

    await Assert.ThrowsAsync<TimeoutException>(() => catalog.RelaysAsync(CameraId, default));
    Assert.Null(presence.TryGetDevices(CameraId, DeviceIoKind.Relay));

    reader.Failure = null;
    reader.Relays = [new OnvifRelayOutput(Relay1, "bistable", "closed", 0)];
    var relays = await catalog.RelaysAsync(CameraId, default);

    // The failure was not remembered: the next request asked the camera again.
    Assert.Single(relays);
    Assert.Equal(2, reader.RelayReads);
  }

  // A wall mounts every tile at once. Without this, the first open of the wall would reproduce
  // exactly the burst of ONVIF requests the cache exists to remove.
  [Fact]
  public async Task ConcurrentColdReadsShareTheSameCameraRequest()
  {
    var presence = new FakePresence();
    var gate = new TaskCompletionSource();
    var reader = new FakeReader
    {
      Relays = [new OnvifRelayOutput(Relay1, "bistable", "closed", 0)],
      Gate = gate.Task,
    };
    var catalog = Catalog(presence, reader);

    var first = catalog.RelaysAsync(CameraId, default);
    var second = catalog.RelaysAsync(CameraId, default);
    gate.SetResult();

    var lists = await Task.WhenAll(first, second);

    Assert.All(lists, list => Assert.Equal(Relay1, Assert.Single(list).Token));
    Assert.Equal(1, reader.RelayReads);
  }

  [Fact]
  public async Task ServingACachedListKeepsTheSubscriptionOpen()
  {
    var presence = new FakePresence();
    // A subscription that does not finish opening within the test: the answer must not wait for it.
    var watchStarted = new TaskCompletionSource();
    presence.WatchStarted = watchStarted.Task;
    presence.Devices[(CameraId, DeviceIoKind.Relay)] = [Relay(Relay1)];
    var catalog = Catalog(presence, new FakeReader());

    var relays = await catalog.RelaysAsync(CameraId, default).WaitAsync(TimeSpan.FromSeconds(2));

    Assert.Single(relays);
    Assert.Equal(1, presence.EnsureCalls);
    watchStarted.SetResult();
  }

  [Fact]
  public async Task ARecordedRelaySettingIsServedEvenWhenTheCameraDoesNotAnswer()
  {
    var presence = new FakePresence();
    presence.Devices[(CameraId, DeviceIoKind.Relay)] = [Relay(Relay1)];
    var reader = new FakeReader { Failure = new TimeoutException("no answer") };
    var catalog = Catalog(presence, reader);

    await catalog.RecordRelaySettingsAsync(CameraId, Relay1, "monostable", "open", 1500, default);

    // The write succeeded at the camera; a failed reconciliation must not undo it, and the panel
    // that reloads immediately after saving sees what it saved.
    var relay = Assert.Single(await catalog.RelaysAsync(CameraId, default));
    Assert.Equal(Relay(Relay1, "open", "monostable", 1500), relay);
  }

  [Fact]
  public async Task TheCameraAnswerReplacesTheRecordedRelaySetting()
  {
    var presence = new FakePresence();
    presence.Devices[(CameraId, DeviceIoKind.Relay)] = [Relay(Relay1)];
    // The camera clamped the delay it was given.
    var reader = new FakeReader
    {
      Relays = [new OnvifRelayOutput(Relay1, "monostable", "open", 900)],
    };
    var catalog = Catalog(presence, reader);

    await catalog.RecordRelaySettingsAsync(CameraId, Relay1, "monostable", "open", 1500, default);

    var relay = Assert.Single(await catalog.RelaysAsync(CameraId, default));
    Assert.Equal(Relay(Relay1, "open", "monostable", 900), relay);
  }

  private sealed class FakePresence : IDeviceIoPresence
  {
    private readonly Dictionary<(string CameraId, DeviceIoKind Kind), long> _versions = [];

    public Dictionary<(string CameraId, DeviceIoKind Kind), IReadOnlyList<DeviceDescriptor>> Devices { get; } = [];

    public int EnsureCalls { get; private set; }

    public Task WatchStarted { get; set; } = Task.CompletedTask;

    public long ListVersion(string cameraId, DeviceIoKind kind) =>
      _versions.GetValueOrDefault((cameraId, kind));

    public Task ReportDevicesAsync(
      string cameraId, DeviceIoKind kind, IReadOnlyList<DeviceDescriptor> devices, long readVersion)
    {
      if (ListVersion(cameraId, kind) != readVersion)
        return Task.CompletedTask;

      Devices[(cameraId, kind)] = devices;
      return Task.CompletedTask;
    }

    public DeviceIoSnapshot? TryGetSnapshot(string cameraId, DeviceIoKind kind, string token) => null;

    public IReadOnlyList<DeviceDescriptor>? TryGetDevices(string cameraId, DeviceIoKind kind) =>
      Devices.GetValueOrDefault((cameraId, kind));

    public async Task EnsureWatchingAsync(string cameraId)
    {
      EnsureCalls++;
      await WatchStarted;
    }

    public Task RecordSettingsAsync(string cameraId, DeviceIoKind kind, DeviceDescriptor device)
    {
      _versions[(cameraId, kind)] = ListVersion(cameraId, kind) + 1;
      if (Devices.TryGetValue((cameraId, kind), out var devices))
      {
        Devices[(cameraId, kind)] = devices
          .Select(d => string.Equals(d.Token, device.Token, StringComparison.Ordinal) ? device : d)
          .ToList();
      }
      return Task.CompletedTask;
    }

    public Task ForgetCamerasNotIn(IReadOnlySet<string> cameras) => Task.CompletedTask;
  }

  private sealed class FakeReader : IDeviceIoReader
  {
    private int _relayReads;
    private int _inputReads;

    public List<OnvifRelayOutput> Relays { get; set; } = [];
    public List<OnvifDigitalInput> Inputs { get; set; } = [];

    /// <summary>Stands in for a camera that does not answer.</summary>
    public Exception? Failure { get; set; }

    /// <summary>Holds the read open, so two callers can be made to overlap.</summary>
    public Task Gate { get; set; } = Task.CompletedTask;

    public int RelayReads => Volatile.Read(ref _relayReads);
    public int InputReads => Volatile.Read(ref _inputReads);

    public async Task<List<OnvifRelayOutput>> GetRelayOutputsAsync(string cameraId, CancellationToken ct)
    {
      Interlocked.Increment(ref _relayReads);
      await Gate;
      if (Failure is { } failure) throw failure;
      return Relays;
    }

    public async Task<List<OnvifDigitalInput>> GetDigitalInputsAsync(string cameraId, CancellationToken ct)
    {
      Interlocked.Increment(ref _inputReads);
      await Gate;
      if (Failure is { } failure) throw failure;
      return Inputs;
    }

    public Task<OnvifDeviceInfo?> GetDeviceInfoAsync(string cameraId, CancellationToken ct) =>
      Task.FromResult<OnvifDeviceInfo?>(null);
  }
}
