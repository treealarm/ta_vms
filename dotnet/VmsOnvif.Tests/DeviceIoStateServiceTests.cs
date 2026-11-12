using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using OnvifLib;
using VmsOnvif;
using web_srv.Types.Actors;

namespace VmsOnvif.Tests;

// ONVIF has no getter for a relay's or an input's live state: it exists only in Device/Trigger
// property events, which the watcher replays as Initialized on subscription and then reports as
// Changed. So the service has three jobs worth testing — when a camera is watched at all, what it
// keeps of what the camera said, and what it announces to the map. All three are driven here
// directly: presence reports stand in for the settings calls, a fake watcher for the camera.
public class DeviceIoStateServiceTests
{
  private const string CameraId = "cam1";

  private static DeviceIoStateService Service(FakeFactory factory, FakeConnections? connections = null) =>
    new(connections ?? new FakeConnections(), factory, NullLogger<DeviceIoStateService>.Instance);

  private static DeviceDescriptor Relay(string token, string? idleState = "closed") =>
    new(token, idleState);

  private static DeviceDescriptor Input(string token, string? idleState = null) =>
    new(token, idleState);

  private static async Task WaitForDebounceAsync() =>
    await Task.Delay(DeviceIoStateService.Debounce + TimeSpan.FromMilliseconds(250));

  [Fact]
  public async Task AReportedRelayOpensTheSubscription()
  {
    var factory = new FakeFactory();
    await using var service = Service(factory);

    await service.ReportDevicesAsync(CameraId, DeviceIoKind.Relay, [Relay("relay1")]);

    Assert.Single(factory.Created);
  }

  [Fact]
  public async Task ACameraWithoutIoIsNotWatched()
  {
    var factory = new FakeFactory();
    await using var service = Service(factory);

    await service.ReportDevicesAsync(CameraId, DeviceIoKind.Relay, []);
    await service.ReportDevicesAsync(CameraId, DeviceIoKind.DigitalInput, []);

    Assert.Empty(factory.Created);
  }

  [Fact]
  public async Task APollingCallerDoesNotOpenASecondSubscription()
  {
    var factory = new FakeFactory();
    await using var service = Service(factory);

    await service.ReportDevicesAsync(CameraId, DeviceIoKind.Relay, [Relay("relay1"), Relay("relay2")]);
    await service.ReportDevicesAsync(CameraId, DeviceIoKind.Relay, [Relay("relay1"), Relay("relay2")]);
    await service.ReportDevicesAsync(CameraId, DeviceIoKind.DigitalInput, [Input("input1")]);

    Assert.Single(factory.Created);
  }

  [Fact]
  public async Task TheRelayListGoingEmptyLeavesTheInputsWatched()
  {
    var factory = new FakeFactory();
    await using var service = Service(factory);
    await service.ReportDevicesAsync(CameraId, DeviceIoKind.Relay, [Relay("relay1")]);
    await service.ReportDevicesAsync(CameraId, DeviceIoKind.DigitalInput, [Input("input1"), Input("input2")]);

    await service.ReportDevicesAsync(CameraId, DeviceIoKind.Relay, []);

    Assert.False(factory.Created[0].Disposed);
  }

  [Fact]
  public async Task TheSubscriptionClosesOnceBothListsAreEmpty()
  {
    var factory = new FakeFactory();
    await using var service = Service(factory);
    await service.ReportDevicesAsync(CameraId, DeviceIoKind.Relay, [Relay("relay1")]);
    await service.ReportDevicesAsync(CameraId, DeviceIoKind.DigitalInput, [Input("input1")]);
    factory.Created[0].Report(State(DeviceIoKind.Relay, "relay1", true));

    await service.ReportDevicesAsync(CameraId, DeviceIoKind.Relay, []);
    await service.ReportDevicesAsync(CameraId, DeviceIoKind.DigitalInput, []);

    Assert.True(factory.Created[0].Disposed);
    Assert.Null(service.TryGetLogicalState(CameraId, DeviceIoKind.Relay, "relay1"));
  }

  [Fact]
  public async Task AnInitializedStateIsTheLiveValue()
  {
    var factory = new FakeFactory();
    await using var service = Service(factory);
    await service.ReportDevicesAsync(CameraId, DeviceIoKind.Relay, [Relay("relay1")]);

    factory.Created[0].Report(State(DeviceIoKind.Relay, "relay1", true, "Initialized"));

    Assert.True(service.TryGetLogicalState(CameraId, DeviceIoKind.Relay, "relay1"));
  }

  [Fact]
  public async Task AChangedStateReplacesTheInitializedOne()
  {
    var factory = new FakeFactory();
    await using var service = Service(factory);
    await service.ReportDevicesAsync(CameraId, DeviceIoKind.Relay, [Relay("relay1")]);
    factory.Created[0].Report(State(DeviceIoKind.Relay, "relay1", true, "Initialized"));

    factory.Created[0].Report(State(DeviceIoKind.Relay, "relay1", false, "Changed"));
    await WaitForDebounceAsync();

    Assert.False(service.TryGetLogicalState(CameraId, DeviceIoKind.Relay, "relay1"));
  }

  [Fact]
  public async Task AStateThatArrivedWhileTheSubscriptionWasBeingAdoptedIsKept()
  {
    // The Initialized replay can reach the watcher before the service has attached its handler.
    var factory = new FakeFactory { Preloaded = State(DeviceIoKind.DigitalInput, "input1", true) };
    await using var service = Service(factory);

    await service.ReportDevicesAsync(CameraId, DeviceIoKind.DigitalInput, [Input("input1")]);

    Assert.True(service.TryGetLogicalState(CameraId, DeviceIoKind.DigitalInput, "input1"));
  }

  [Fact]
  public async Task Snapshots_CarryEveryKnownPartAndItsContact()
  {
    var factory = new FakeFactory();
    await using var service = Service(factory);
    await service.ReportDevicesAsync(CameraId, DeviceIoKind.Relay, [Relay("relay1")]);
    await service.ReportDevicesAsync(CameraId, DeviceIoKind.DigitalInput, [Input("input1", "open")]);
    factory.Created[0].Report(State(DeviceIoKind.Relay, "relay1", true, "Initialized"));
    factory.Created[0].Report(State(DeviceIoKind.DigitalInput, "input1", false, "Initialized"));

    var snapshots = service.Snapshots().ToList();

    var relay = snapshots.Single(s => s.Snapshot.Kind == DeviceIoKind.Relay);
    Assert.Equal((CameraId, "relay1", true), (relay.CameraId, relay.Snapshot.Token, relay.Snapshot.LogicalState));
    var input = snapshots.Single(s => s.Snapshot.Kind == DeviceIoKind.DigitalInput);
    // Idle state open: the open circuit is the idle one.
    Assert.Equal("open", input.Snapshot.Contact);
  }

  [Fact]
  public async Task ADeviceTheCameraHasNotMentionedHasNoState()
  {
    var factory = new FakeFactory();
    await using var service = Service(factory);
    await service.ReportDevicesAsync(CameraId, DeviceIoKind.Relay, [Relay("relay1")]);
    factory.Created[0].Report(State(DeviceIoKind.Relay, "relay1", true));

    Assert.Null(service.TryGetLogicalState(CameraId, DeviceIoKind.Relay, "relay2"));
    Assert.Null(service.TryGetLogicalState(CameraId, DeviceIoKind.DigitalInput, "relay1"));
  }

  [Fact]
  public async Task AConfigurationChangeDropsTheSubscriptionForTheNextReadToRebuild()
  {
    var factory = new FakeFactory();
    await using var service = Service(factory);
    await service.ReportDevicesAsync(CameraId, DeviceIoKind.Relay, [Relay("relay1")]);
    var first = factory.Created[0];

    await service.ForgetAsync(CameraId);
    await service.ReportDevicesAsync(CameraId, DeviceIoKind.Relay, [Relay("relay1")]);

    Assert.True(first.Disposed);
    Assert.Equal(2, factory.Created.Count);
  }

  [Fact]
  public async Task ACameraThatCannotBeWatchedIsRetriedOnTheNextReport()
  {
    var factory = new FakeFactory { ReturnNull = true };
    await using var service = Service(factory);

    await service.ReportDevicesAsync(CameraId, DeviceIoKind.Relay, [Relay("relay1")]);
    Assert.Empty(factory.Created);

    factory.ReturnNull = false;
    await service.ReportDevicesAsync(CameraId, DeviceIoKind.Relay, [Relay("relay1")]);

    Assert.Single(factory.Created);
  }

  [Fact]
  public async Task ACameraWithNoOnvifConfigurationIsNotWatched()
  {
    var factory = new FakeFactory();
    await using var service = Service(factory, new FakeConnections(configured: false));

    await service.ReportDevicesAsync(CameraId, DeviceIoKind.Relay, [Relay("relay1")]);

    Assert.Empty(factory.Created);
  }

  // ── What the map is told ───────────────────────────────────────────────────────────────────

  [Fact]
  public async Task ANormallyOpenInputClosesItsContactWhenTheLogicalStateIsTrue()
  {
    var factory = new FakeFactory();
    await using var service = Service(factory);
    var events = Capture(service);
    await service.ReportDevicesAsync(CameraId, DeviceIoKind.DigitalInput, [Input("input1", "open")]);

    factory.Created[0].Report(State(DeviceIoKind.DigitalInput, "input1", true, "Initialized"));

    var evt = Assert.Single(events);
    Assert.Equal("closed", evt.Contact);
    Assert.True(evt.PolarityKnown);
    Assert.True(evt.Initialized);
  }

  [Fact]
  public async Task ANormallyClosedInputOpensItsContactWhenTheLogicalStateIsTrue()
  {
    var factory = new FakeFactory();
    await using var service = Service(factory);
    var events = Capture(service);
    await service.ReportDevicesAsync(CameraId, DeviceIoKind.DigitalInput, [Input("input1", "closed")]);

    factory.Created[0].Report(State(DeviceIoKind.DigitalInput, "input1", true, "Initialized"));

    var evt = Assert.Single(events);
    Assert.Equal("open", evt.Contact);
  }

  [Fact]
  public async Task AnInputWithNoIdleStateReportsNoContact()
  {
    var factory = new FakeFactory();
    await using var service = Service(factory);
    var events = Capture(service);
    await service.ReportDevicesAsync(CameraId, DeviceIoKind.DigitalInput, [Input("input1", null)]);

    factory.Created[0].Report(State(DeviceIoKind.DigitalInput, "input1", true, "Initialized"));

    var evt = Assert.Single(events);
    Assert.Null(evt.Contact);
    Assert.False(evt.PolarityKnown);
  }

  [Fact]
  public async Task ARepeatedValueIsNotASecondEvent()
  {
    var factory = new FakeFactory();
    await using var service = Service(factory);
    var events = Capture(service);
    await service.ReportDevicesAsync(CameraId, DeviceIoKind.DigitalInput, [Input("input1", "closed")]);

    factory.Created[0].Report(State(DeviceIoKind.DigitalInput, "input1", false, "Initialized"));
    factory.Created[0].Report(State(DeviceIoKind.DigitalInput, "input1", false, "Changed"));
    await WaitForDebounceAsync();

    Assert.Single(events);
  }

  [Fact]
  public async Task AChangedValueIsPublishedAfterTheDebounce()
  {
    var factory = new FakeFactory();
    await using var service = Service(factory);
    var events = Capture(service);
    await service.ReportDevicesAsync(CameraId, DeviceIoKind.DigitalInput, [Input("input1", "closed")]);

    factory.Created[0].Report(State(DeviceIoKind.DigitalInput, "input1", false, "Initialized"));
    factory.Created[0].Report(State(DeviceIoKind.DigitalInput, "input1", true, "Changed"));

    // Nothing yet: the debounce window is still open.
    Assert.Single(events);
    await WaitForDebounceAsync();

    var changed = events.Last();
    Assert.False(changed.Initialized);
    Assert.Equal("open", changed.Contact);
    Assert.Equal("closed", changed.Previous);
  }

  [Fact]
  public async Task FlappingWithinTheWindowIsOneEvent()
  {
    var factory = new FakeFactory();
    await using var service = Service(factory);
    var events = Capture(service);
    await service.ReportDevicesAsync(CameraId, DeviceIoKind.DigitalInput, [Input("input1", "closed")]);

    factory.Created[0].Report(State(DeviceIoKind.DigitalInput, "input1", false, "Initialized"));
    factory.Created[0].Report(State(DeviceIoKind.DigitalInput, "input1", true, "Changed"));
    factory.Created[0].Report(State(DeviceIoKind.DigitalInput, "input1", false, "Changed"));
    await WaitForDebounceAsync();

    // The initialized snapshot and one transition: the value ended where it started, so nothing
    // after the window is worth telling the map.
    Assert.Single(events);
  }

  [Fact]
  public async Task AChangedIdleStateRecomputesTheContactAtOnce()
  {
    var factory = new FakeFactory();
    await using var service = Service(factory);
    var events = Capture(service);
    await service.ReportDevicesAsync(CameraId, DeviceIoKind.DigitalInput, [Input("input1", "closed")]);
    factory.Created[0].Report(State(DeviceIoKind.DigitalInput, "input1", true, "Initialized"));
    Assert.Equal("open", events.Single().Contact);

    // The operator flips the polarity; the camera has not sent a new state, but the meaning of the
    // state it already sent changed.
    await service.ReportDevicesAsync(CameraId, DeviceIoKind.DigitalInput, [Input("input1", "open")]);

    var polarity = events.Last();
    Assert.Equal("closed", polarity.Contact);
    Assert.Equal("open", polarity.Previous);
    Assert.False(polarity.Initialized);
  }

  [Fact]
  public async Task ForgettingACameraAnnouncesItsPartsAsAbsent()
  {
    var factory = new FakeFactory();
    await using var service = Service(factory);
    ForgottenParts? forgotten = null;
    service.PartsForgotten += f => forgotten = f;

    await service.ReportDevicesAsync(CameraId, DeviceIoKind.DigitalInput, [Input("input1", "closed")]);
    await service.ReportDevicesAsync(CameraId, DeviceIoKind.Relay, [Relay("relay1")]);

    await service.ForgetAsync(CameraId);

    Assert.NotNull(forgotten);
    Assert.Equal(CameraId, forgotten!.CameraId);
    Assert.Equal(2, forgotten.Parts.Count);
    Assert.Contains(forgotten.Parts, p => p.Descriptor.Token == "input1");
    Assert.Contains(forgotten.Parts, p => p.Descriptor.Token == "relay1");
  }

  [Fact]
  public async Task ANodeWideForgetDoesNotAnnounceEveryCamera()
  {
    var factory = new FakeFactory();
    await using var service = Service(factory);
    var announced = 0;
    service.PartsForgotten += _ => announced++;

    await service.ReportDevicesAsync(CameraId, DeviceIoKind.DigitalInput, [Input("input1", "closed")]);
    await service.ForgetAllAsync();

    Assert.Equal(0, announced);
  }

  // The operator's command is the state the relay was told to be: it is announced at once, because
  // the camera may report its own change only on the next delivery of its subscription.
  [Fact]
  public async Task ACommandedRelayStateIsAnnouncedWithoutWaitingForTheCamera()
  {
    var factory = new FakeFactory { Preloaded = State(DeviceIoKind.Relay, "relay1", false) };
    await using var service = Service(factory);
    var events = Capture(service);
    await service.ReportDevicesAsync(CameraId, DeviceIoKind.Relay, [Relay("relay1")]);
    events.Clear(); // the subscription's Initialized replay

    await service.CommandedRelayStateAsync(CameraId, "relay1", true);

    var raised = Assert.Single(events);
    Assert.True(raised.Active);
    Assert.False(raised.Initialized);
    Assert.True(service.TryGetLogicalState(CameraId, DeviceIoKind.Relay, "relay1"));
  }

  // The camera does report its own state, late; the command already said it, so it is not a
  // second transition and the map must not see one.
  [Fact]
  public async Task TheCamerasLateReportOfTheCommandIsNotASecondTransition()
  {
    var factory = new FakeFactory { Preloaded = State(DeviceIoKind.Relay, "relay1", false) };
    await using var service = Service(factory);
    var events = Capture(service);
    await service.ReportDevicesAsync(CameraId, DeviceIoKind.Relay, [Relay("relay1")]);
    events.Clear();

    await service.CommandedRelayStateAsync(CameraId, "relay1", true);
    factory.Created[0].Report(State(DeviceIoKind.Relay, "relay1", true));
    await WaitForDebounceAsync();

    Assert.Single(events);
  }

  // A relay the subscription has not reported is left alone: there is no known state to correct,
  // and its first report will bring it in as the camera says.
  [Fact]
  public async Task ACommandForAnUnknownRelayIsIgnored()
  {
    var factory = new FakeFactory();
    await using var service = Service(factory);
    var events = Capture(service);
    await service.ReportDevicesAsync(CameraId, DeviceIoKind.Relay, [Relay("relay1")]);
    events.Clear();

    await service.CommandedRelayStateAsync(CameraId, "relay9", true);

    Assert.Empty(events);
    Assert.Null(service.TryGetLogicalState(CameraId, DeviceIoKind.Relay, "relay9"));
  }

  private static ConcurrentQueue<DeviceEvent> Capture(DeviceIoStateService service)
  {
    var events = new ConcurrentQueue<DeviceEvent>();
    service.DeviceEventRaised += events.Enqueue;
    return events;
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
