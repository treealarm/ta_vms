using Microsoft.Extensions.Logging.Abstractions;
using OnvifLib;
using web_srv.Types.Actors;

namespace VmsOnvif.Tests;

// ONVIF has no getter for a relay's or an input's live state: it exists only in Device/Trigger
// property events, which the watcher replays as Initialized on subscription and then reports as
// Changed. So the service has two jobs worth testing — when a camera is watched at all, and what it
// keeps of what the camera said. Both are driven here directly: presence reports stand in for the
// settings calls, a fake watcher stands in for the camera.
public class DeviceIoStateServiceTests
{
  private const string CameraId = "cam1";

  private static DeviceIoStateService Service(FakeFactory factory, FakeConnections? connections = null) =>
    new(connections ?? new FakeConnections(), factory, NullLogger<DeviceIoStateService>.Instance);

  [Fact]
  public async Task AReportedRelayOpensTheSubscription()
  {
    var factory = new FakeFactory();
    await using var service = Service(factory);

    await service.ReportDevicePresenceAsync(CameraId, DeviceIoKind.Relay, 3);

    Assert.Single(factory.Created);
  }

  [Fact]
  public async Task ACameraWithoutIoIsNotWatched()
  {
    var factory = new FakeFactory();
    await using var service = Service(factory);

    await service.ReportDevicePresenceAsync(CameraId, DeviceIoKind.Relay, 0);
    await service.ReportDevicePresenceAsync(CameraId, DeviceIoKind.DigitalInput, 0);

    Assert.Empty(factory.Created);
  }

  [Fact]
  public async Task APollingCallerDoesNotOpenASecondSubscription()
  {
    var factory = new FakeFactory();
    await using var service = Service(factory);

    await service.ReportDevicePresenceAsync(CameraId, DeviceIoKind.Relay, 3);
    await service.ReportDevicePresenceAsync(CameraId, DeviceIoKind.Relay, 3);
    await service.ReportDevicePresenceAsync(CameraId, DeviceIoKind.DigitalInput, 1);

    Assert.Single(factory.Created);
  }

  [Fact]
  public async Task TheRelayListGoingEmptyLeavesTheInputsWatched()
  {
    var factory = new FakeFactory();
    await using var service = Service(factory);
    await service.ReportDevicePresenceAsync(CameraId, DeviceIoKind.Relay, 3);
    await service.ReportDevicePresenceAsync(CameraId, DeviceIoKind.DigitalInput, 2);

    await service.ReportDevicePresenceAsync(CameraId, DeviceIoKind.Relay, 0);

    Assert.False(factory.Created[0].Disposed);
  }

  [Fact]
  public async Task TheSubscriptionClosesOnceBothListsAreEmpty()
  {
    var factory = new FakeFactory();
    await using var service = Service(factory);
    await service.ReportDevicePresenceAsync(CameraId, DeviceIoKind.Relay, 3);
    await service.ReportDevicePresenceAsync(CameraId, DeviceIoKind.DigitalInput, 2);
    factory.Created[0].Report(State(DeviceIoKind.Relay, "relay1", true));

    await service.ReportDevicePresenceAsync(CameraId, DeviceIoKind.Relay, 0);
    await service.ReportDevicePresenceAsync(CameraId, DeviceIoKind.DigitalInput, 0);

    Assert.True(factory.Created[0].Disposed);
    Assert.Null(service.TryGetLogicalState(CameraId, DeviceIoKind.Relay, "relay1"));
  }

  [Fact]
  public async Task AnInitializedStateIsTheLiveValue()
  {
    var factory = new FakeFactory();
    await using var service = Service(factory);
    await service.ReportDevicePresenceAsync(CameraId, DeviceIoKind.Relay, 3);

    factory.Created[0].Report(State(DeviceIoKind.Relay, "relay1", true, "Initialized"));

    Assert.True(service.TryGetLogicalState(CameraId, DeviceIoKind.Relay, "relay1"));
  }

  [Fact]
  public async Task AChangedStateReplacesTheInitializedOne()
  {
    var factory = new FakeFactory();
    await using var service = Service(factory);
    await service.ReportDevicePresenceAsync(CameraId, DeviceIoKind.Relay, 3);
    factory.Created[0].Report(State(DeviceIoKind.Relay, "relay1", true, "Initialized"));

    factory.Created[0].Report(State(DeviceIoKind.Relay, "relay1", false, "Changed"));

    Assert.False(service.TryGetLogicalState(CameraId, DeviceIoKind.Relay, "relay1"));
  }

  [Fact]
  public async Task AStateThatArrivedWhileTheSubscriptionWasBeingAdoptedIsKept()
  {
    // The Initialized replay can reach the watcher before the service has attached its handler.
    var factory = new FakeFactory { Preloaded = State(DeviceIoKind.DigitalInput, "input1", true) };
    await using var service = Service(factory);

    await service.ReportDevicePresenceAsync(CameraId, DeviceIoKind.DigitalInput, 1);

    Assert.True(service.TryGetLogicalState(CameraId, DeviceIoKind.DigitalInput, "input1"));
  }

  [Fact]
  public async Task ADeviceTheCameraHasNotMentionedHasNoState()
  {
    var factory = new FakeFactory();
    await using var service = Service(factory);
    await service.ReportDevicePresenceAsync(CameraId, DeviceIoKind.Relay, 3);
    factory.Created[0].Report(State(DeviceIoKind.Relay, "relay1", true));

    Assert.Null(service.TryGetLogicalState(CameraId, DeviceIoKind.Relay, "relay2"));
    Assert.Null(service.TryGetLogicalState(CameraId, DeviceIoKind.DigitalInput, "relay1"));
  }

  [Fact]
  public async Task AConfigurationChangeDropsTheSubscriptionForTheNextReadToRebuild()
  {
    var factory = new FakeFactory();
    await using var service = Service(factory);
    await service.ReportDevicePresenceAsync(CameraId, DeviceIoKind.Relay, 1);
    var first = factory.Created[0];

    await service.ForgetAsync(CameraId);
    await service.ReportDevicePresenceAsync(CameraId, DeviceIoKind.Relay, 1);

    Assert.True(first.Disposed);
    Assert.Equal(2, factory.Created.Count);
  }

  [Fact]
  public async Task ACameraThatCannotBeWatchedIsRetriedOnTheNextReport()
  {
    var factory = new FakeFactory { ReturnNull = true };
    await using var service = Service(factory);

    await service.ReportDevicePresenceAsync(CameraId, DeviceIoKind.Relay, 1);
    Assert.Empty(factory.Created);

    factory.ReturnNull = false;
    await service.ReportDevicePresenceAsync(CameraId, DeviceIoKind.Relay, 1);

    Assert.Single(factory.Created);
  }

  [Fact]
  public async Task ACameraWithNoOnvifConfigurationIsNotWatched()
  {
    var factory = new FakeFactory();
    await using var service = Service(factory, new FakeConnections(configured: false));

    await service.ReportDevicePresenceAsync(CameraId, DeviceIoKind.Relay, 1);

    Assert.Empty(factory.Created);
  }

  private static DeviceIoState State(
    DeviceIoKind kind, string token, bool logical, string operation = "Changed") =>
    new(kind, token, logical, kind == DeviceIoKind.Relay ? logical : null, operation, null);

  private sealed class FakeConnections(bool configured = true) : ICameraConnectionSource
  {
    private readonly CameraConnData? _data = configured
      ? new CameraConnData(new CameraConnInfo("10.0.0.1", 80, "user", "pass"), [])
      : null;

    public Task<CameraConnData?> GetAsync(string cameraId, CancellationToken ct) =>
      Task.FromResult(_data);
  }

  private sealed class FakeFactory : IDeviceIoWatcherFactory
  {
    public List<FakeWatcher> Created { get; } = [];

    /// <summary>A state the watcher already holds when the service adopts it.</summary>
    public DeviceIoState? Preloaded { get; init; }

    /// <summary>Stands in for a camera with no event service, or one that could not be reached.</summary>
    public bool ReturnNull { get; set; }

    public Task<IDeviceIoWatcher?> CreateAsync(CameraConnInfo conn)
    {
      if (ReturnNull)
        return Task.FromResult<IDeviceIoWatcher?>(null);

      var watcher = new FakeWatcher();
      if (Preloaded is { } preloaded)
        watcher.Preload(preloaded);
      Created.Add(watcher);
      return Task.FromResult<IDeviceIoWatcher?>(watcher);
    }
  }

  private sealed class FakeWatcher : IDeviceIoWatcher
  {
    private readonly Dictionary<(DeviceIoKind Kind, string Token), DeviceIoState> _states = [];

    public event Action<DeviceIoState>? StateChanged;

    public IReadOnlyCollection<DeviceIoState> States => _states.Values.ToArray();

    public bool Disposed { get; private set; }

    public void Report(DeviceIoState state)
    {
      _states[(state.Kind, state.Token)] = state;
      StateChanged?.Invoke(state);
    }

    /// <summary>A state that reached the camera's stream before anyone was listening.</summary>
    public void Preload(DeviceIoState state) => _states[(state.Kind, state.Token)] = state;

    public ValueTask DisposeAsync()
    {
      Disposed = true;
      return ValueTask.CompletedTask;
    }
  }
}
