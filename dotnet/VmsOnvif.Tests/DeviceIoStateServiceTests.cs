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

  private static DeviceIoStateService Service(
    FakeFactory factory,
    FakeConnections? connections = null,
    FakeCommander? commander = null,
    FakeTime? time = null) =>
    new(connections ?? new FakeConnections(), factory,
        commander ?? new FakeCommander(), time ?? new FakeTime(),
        NullLogger<DeviceIoStateService>.Instance);

  private static DeviceDescriptor Relay(string token, string? idleState = "closed") =>
    new(token, idleState);

  private static DeviceDescriptor Input(string token, string? idleState = null) =>
    new(token, idleState);

  /// <summary>A report of the list as it stands now: the version is the one a reader would have
  /// captured just before asking the camera.</summary>
  private static Task Report(
    DeviceIoStateService service, DeviceIoKind kind, params DeviceDescriptor[] devices) =>
    service.ReportDevicesAsync(CameraId, kind, devices, service.ListVersion(CameraId, kind));

  private static async Task WaitForDebounceAsync() =>
    await Task.Delay(DeviceIoStateService.Debounce + TimeSpan.FromMilliseconds(250));

  [Fact]
  public async Task AReportedRelayOpensTheSubscription()
  {
    var factory = new FakeFactory();
    await using var service = Service(factory);

    await Report(service, DeviceIoKind.Relay, [Relay("relay1")]);

    Assert.Single(factory.Created);
  }

  [Fact]
  public async Task ACameraWithoutIoIsNotWatched()
  {
    var factory = new FakeFactory();
    await using var service = Service(factory);

    await Report(service, DeviceIoKind.Relay, []);
    await Report(service, DeviceIoKind.DigitalInput, []);

    Assert.Empty(factory.Created);
  }

  [Fact]
  public async Task APollingCallerDoesNotOpenASecondSubscription()
  {
    var factory = new FakeFactory();
    await using var service = Service(factory);

    await Report(service, DeviceIoKind.Relay, [Relay("relay1"), Relay("relay2")]);
    await Report(service, DeviceIoKind.Relay, [Relay("relay1"), Relay("relay2")]);
    await Report(service, DeviceIoKind.DigitalInput, [Input("input1")]);

    Assert.Single(factory.Created);
  }

  [Fact]
  public async Task TheRelayListGoingEmptyLeavesTheInputsWatched()
  {
    var factory = new FakeFactory();
    await using var service = Service(factory);
    await Report(service, DeviceIoKind.Relay, [Relay("relay1")]);
    await Report(service, DeviceIoKind.DigitalInput, [Input("input1"), Input("input2")]);

    await Report(service, DeviceIoKind.Relay, []);

    Assert.False(factory.Created[0].Disposed);
  }

  [Fact]
  public async Task TheSubscriptionClosesOnceBothListsAreEmpty()
  {
    var factory = new FakeFactory();
    await using var service = Service(factory);
    await Report(service, DeviceIoKind.Relay, [Relay("relay1")]);
    await Report(service, DeviceIoKind.DigitalInput, [Input("input1")]);
    factory.Created[0].Report(State(DeviceIoKind.Relay, "relay1", true));

    await Report(service, DeviceIoKind.Relay, []);
    await Report(service, DeviceIoKind.DigitalInput, []);

    Assert.True(factory.Created[0].Disposed);
    Assert.Null(service.TryGetLogicalState(CameraId, DeviceIoKind.Relay, "relay1"));
  }

  [Fact]
  public async Task AnInitializedStateIsTheLiveValue()
  {
    var factory = new FakeFactory();
    await using var service = Service(factory);
    await Report(service, DeviceIoKind.Relay, [Relay("relay1")]);

    factory.Created[0].Report(State(DeviceIoKind.Relay, "relay1", true, "Initialized"));

    Assert.True(service.TryGetLogicalState(CameraId, DeviceIoKind.Relay, "relay1"));
  }

  [Fact]
  public async Task AChangedStateReplacesTheInitializedOne()
  {
    var factory = new FakeFactory();
    await using var service = Service(factory);
    await Report(service, DeviceIoKind.Relay, [Relay("relay1")]);
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

    await Report(service, DeviceIoKind.DigitalInput, [Input("input1")]);

    Assert.True(service.TryGetLogicalState(CameraId, DeviceIoKind.DigitalInput, "input1"));
  }

  // ...and it is what the camera says now, so the map hears it too: a contact that changed while
  // the subscription was being rebuilt would otherwise stay stale until the part next moved.
  [Fact]
  public async Task AStateAdoptedBeforeTheSubscriptionIsWatchedIsAnnouncedToTheMap()
  {
    var factory = new FakeFactory { Preloaded = State(DeviceIoKind.DigitalInput, "input1", true) };
    await using var service = Service(factory);
    var events = Capture(service);

    await Report(service, DeviceIoKind.DigitalInput, [Input("input1", "open")]);

    var announced = Assert.Single(events);
    Assert.True(announced.Initialized);
    Assert.Equal("closed", announced.Contact);
  }

  [Fact]
  public async Task Snapshots_CarryEveryKnownPartAndItsContact()
  {
    var factory = new FakeFactory();
    await using var service = Service(factory);
    await Report(service, DeviceIoKind.Relay, [Relay("relay1")]);
    await Report(service, DeviceIoKind.DigitalInput, [Input("input1", "open")]);
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
    await Report(service, DeviceIoKind.Relay, [Relay("relay1")]);
    factory.Created[0].Report(State(DeviceIoKind.Relay, "relay1", true));

    Assert.Null(service.TryGetLogicalState(CameraId, DeviceIoKind.Relay, "relay2"));
    Assert.Null(service.TryGetLogicalState(CameraId, DeviceIoKind.DigitalInput, "relay1"));
  }

  [Fact]
  public async Task AConfigurationChangeDropsTheSubscriptionForTheNextReadToRebuild()
  {
    var factory = new FakeFactory();
    await using var service = Service(factory);
    await Report(service, DeviceIoKind.Relay, [Relay("relay1")]);
    var first = factory.Created[0];

    await service.ForgetSubscriptionAsync(CameraId);
    await Report(service, DeviceIoKind.Relay, [Relay("relay1")]);

    Assert.True(first.Disposed);
    Assert.Equal(2, factory.Created.Count);
  }

  // A configuration change — and the map writes a camera's arming flag on every zone change — may
  // move the camera or change its credentials, but the parts it holds keep their tokens and idle
  // states. Dropping them would make the next input event unpolarised, and the map discards an
  // event whose contact it cannot name until a settings read or the discovery sweep restated the
  // manifest: the contact would be blind for minutes.
  [Fact]
  public async Task AConfigurationChangeKeepsThePartsSoTheirEventsStillCarryAContact()
  {
    var factory = new FakeFactory();
    await using var service = Service(factory);
    var events = Capture(service);
    await Report(service, DeviceIoKind.DigitalInput, [Input("input1", "open")]);
    await Report(service, DeviceIoKind.Relay, [Relay("relay1")]);

    await service.ForgetSubscriptionAsync(CameraId);

    // A relay command reports the relay list and reopens the subscription; the input's own list is
    // not read, so only the kept manifest can give its events a contact.
    await Report(service, DeviceIoKind.Relay, [Relay("relay1")]);
    factory.Created[^1].Report(State(DeviceIoKind.DigitalInput, "input1", false, "Changed"));
    await WaitForDebounceAsync();

    var report = events.Last(raised => raised.Kind == DeviceIoKind.DigitalInput);
    Assert.True(report.PolarityKnown);
    Assert.Equal("open", report.Contact);
  }

  [Fact]
  public async Task ACameraThatCannotBeWatchedIsRetriedOnTheNextReport()
  {
    var factory = new FakeFactory { ReturnNull = true };
    await using var service = Service(factory);

    await Report(service, DeviceIoKind.Relay, [Relay("relay1")]);
    Assert.Empty(factory.Created);

    factory.ReturnNull = false;
    await Report(service, DeviceIoKind.Relay, [Relay("relay1")]);

    Assert.Single(factory.Created);
  }

  [Fact]
  public async Task ACameraWithNoOnvifConfigurationIsNotWatched()
  {
    var factory = new FakeFactory();
    await using var service = Service(factory, new FakeConnections(configured: false));

    await Report(service, DeviceIoKind.Relay, [Relay("relay1")]);

    Assert.Empty(factory.Created);
  }

  // ── What the presence remembers ────────────────────────────────────────────────────────────

  [Fact]
  public async Task TryGetDevicesIsNullUntilThatListIsReported()
  {
    var factory = new FakeFactory();
    await using var service = Service(factory);

    await Report(service, DeviceIoKind.Relay, [Relay("relay1")]);

    Assert.Equal(new[] { Relay("relay1") }, service.TryGetDevices(CameraId, DeviceIoKind.Relay));
    // Not read is not the same as "has none": a list nobody asked the camera for must not be
    // served as empty.
    Assert.Null(service.TryGetDevices(CameraId, DeviceIoKind.DigitalInput));
  }

  // An empty list is the camera's answer. Keeping it means the next tile to open does not read the
  // camera again to learn the same nothing; the subscription has nothing to watch and goes.
  [Fact]
  public async Task ACameraWithBothListsEmptyKeepsItsEmptyPresence()
  {
    var factory = new FakeFactory();
    await using var service = Service(factory);
    await Report(service, DeviceIoKind.Relay, [Relay("relay1")]);
    factory.Created[0].Report(State(DeviceIoKind.Relay, "relay1", true));

    await Report(service, DeviceIoKind.Relay, []);
    await Report(service, DeviceIoKind.DigitalInput, []);

    Assert.Empty(service.TryGetDevices(CameraId, DeviceIoKind.Relay)!);
    Assert.Empty(service.TryGetDevices(CameraId, DeviceIoKind.DigitalInput)!);
    Assert.True(factory.Created[0].Disposed);
  }

  [Fact]
  public async Task AnInFlightReadCannotOverwriteANewerSettingsWrite()
  {
    var factory = new FakeFactory();
    await using var service = Service(factory);
    await Report(service, DeviceIoKind.Relay, [Relay("relay1")]);

    // A read captures the version before asking the camera...
    var readVersion = service.ListVersion(CameraId, DeviceIoKind.Relay);
    await service.RecordSettingsAsync(
      CameraId, DeviceIoKind.Relay, new DeviceDescriptor("relay1", "closed", "monostable", 1500));

    // ...the operator saves new settings meanwhile, and the camera answers with the old ones.
    await service.ReportDevicesAsync(
      CameraId, DeviceIoKind.Relay,
      [new DeviceDescriptor("relay1", "closed", "bistable", 0)], readVersion);

    // The write is what the camera has now; the older read does not undo it.
    Assert.Equal(
      new DeviceDescriptor("relay1", "closed", "monostable", 1500),
      Assert.Single(service.TryGetDevices(CameraId, DeviceIoKind.Relay)!));
  }

  // The logical state did not change, its meaning did: the map must hear the new contact now, not
  // on the camera's next event.
  [Fact]
  public async Task ChangingAnInputIdleStateRecalculatesItsCurrentContact()
  {
    var factory = new FakeFactory();
    await using var service = Service(factory);
    var events = Capture(service);
    await Report(service, DeviceIoKind.DigitalInput, [Input("input1", "closed")]);
    factory.Created[0].Report(State(DeviceIoKind.DigitalInput, "input1", true, "Initialized"));
    Assert.Equal("open", events.Last().Contact);

    await service.RecordSettingsAsync(
      CameraId, DeviceIoKind.DigitalInput, new DeviceDescriptor("input1", "open"));

    var polarity = events.Last();
    Assert.Equal("closed", polarity.Contact);
    Assert.Equal("open", polarity.Previous);
    Assert.False(polarity.Initialized);
  }

  [Fact]
  public async Task EnsuringAWatcherForACameraWithNoIoOpensNothing()
  {
    var factory = new FakeFactory();
    await using var service = Service(factory);
    await Report(service, DeviceIoKind.Relay, []);
    await Report(service, DeviceIoKind.DigitalInput, []);

    await service.EnsureWatchingAsync(CameraId);

    // The cached empty list is served on every open; it must not turn each of them into a
    // subscription to a camera with nothing to report.
    Assert.Empty(factory.Created);
  }

  [Fact]
  public async Task RemovingACameraDisposesItsWatcherAndItsPresence()
  {
    var factory = new FakeFactory();
    await using var service = Service(factory);
    await Report(service, DeviceIoKind.Relay, [Relay("relay1")]);
    await service.ReportDevicesAsync(
      "cam2", DeviceIoKind.Relay, [Relay("relay1")], service.ListVersion("cam2", DeviceIoKind.Relay));
    var watcher = factory.Created[0];

    await service.ForgetCamerasNotIn(new HashSet<string>(StringComparer.Ordinal) { "cam2" });

    Assert.True(watcher.Disposed);
    Assert.Null(service.TryGetDevices(CameraId, DeviceIoKind.Relay));
    Assert.Null(service.TryGetSnapshot(CameraId, DeviceIoKind.Relay, "relay1"));
    // A camera still in the configuration keeps its list.
    Assert.Single(service.TryGetDevices("cam2", DeviceIoKind.Relay)!);
  }

  // ── What the map is told ───────────────────────────────────────────────────────────────────

  [Fact]
  public async Task ANormallyOpenInputClosesItsContactWhenTheLogicalStateIsTrue()
  {
    var factory = new FakeFactory();
    await using var service = Service(factory);
    var events = Capture(service);
    await Report(service, DeviceIoKind.DigitalInput, [Input("input1", "open")]);

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
    await Report(service, DeviceIoKind.DigitalInput, [Input("input1", "closed")]);

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
    await Report(service, DeviceIoKind.DigitalInput, [Input("input1", null)]);

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
    await Report(service, DeviceIoKind.DigitalInput, [Input("input1", "closed")]);

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
    await Report(service, DeviceIoKind.DigitalInput, [Input("input1", "closed")]);

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
    await Report(service, DeviceIoKind.DigitalInput, [Input("input1", "closed")]);

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
    await Report(service, DeviceIoKind.DigitalInput, [Input("input1", "closed")]);
    factory.Created[0].Report(State(DeviceIoKind.DigitalInput, "input1", true, "Initialized"));
    Assert.Equal("open", events.Single().Contact);

    // The operator flips the polarity; the camera has not sent a new state, but the meaning of the
    // state it already sent changed.
    await Report(service, DeviceIoKind.DigitalInput, [Input("input1", "open")]);

    var polarity = events.Last();
    Assert.Equal("closed", polarity.Contact);
    Assert.Equal("open", polarity.Previous);
    Assert.False(polarity.Initialized);
  }

  // A dropped subscription is not a device fact. Forgetting a camera's I/O — because both its
  // lists turned out empty, or a node-wide change may have moved it — forgets the presence and
  // publishes nothing: the next discovery sweep restates the manifest, and that is the only
  // statement allowed to say a part is absent.
  [Fact]
  public async Task ForgettingACameraDropsItsPartsWithoutAnnouncingThem()
  {
    var factory = new FakeFactory();
    await using var service = Service(factory);

    await Report(service, DeviceIoKind.DigitalInput, [Input("input1", "closed")]);
    await Report(service, DeviceIoKind.Relay, [Relay("relay1")]);

    await service.ForgetAsync(CameraId);

    Assert.Null(service.TryGetSnapshot(CameraId, DeviceIoKind.DigitalInput, "input1"));
    Assert.Null(service.TryGetSnapshot(CameraId, DeviceIoKind.Relay, "relay1"));
    Assert.Empty(service.Snapshots());
  }

  [Fact]
  public async Task ANodeWideForgetAlsoForgetsEveryCameraSilently()
  {
    var factory = new FakeFactory();
    await using var service = Service(factory);

    await Report(service, DeviceIoKind.DigitalInput, [Input("input1", "closed")]);
    await service.ForgetAllAsync();

    Assert.Null(service.TryGetSnapshot(CameraId, DeviceIoKind.DigitalInput, "input1"));
    Assert.Empty(service.Snapshots());
  }

  // The operator's command is a wish, not a report: it is shown at once, because the camera may
  // report its own change only on the next delivery of its subscription, but it is marked as
  // unconfirmed until the camera says the relay is really in that state.
  [Fact]
  public async Task ACommandPublishesTheWishAsPending()
  {
    var factory = new FakeFactory { Preloaded = State(DeviceIoKind.Relay, "relay1", false) };
    await using var service = Service(factory);
    var events = Capture(service);
    await Report(service, DeviceIoKind.Relay, [Relay("relay1")]);
    events.Clear(); // the subscription's Initialized replay

    await Command(service, true);

    var raised = Assert.Single(events);
    Assert.True(raised.Active);
    Assert.False(raised.Initialized);
    Assert.True(service.TryGetLogicalState(CameraId, DeviceIoKind.Relay, "relay1"));

    var snapshot = service.TryGetSnapshot(CameraId, DeviceIoKind.Relay, "relay1");
    Assert.True(snapshot?.Pending);
    Assert.True(snapshot?.LogicalState);
  }

  // The camera's own report is what ends the wish. It arrives with the value the wish asked for,
  // so the screens keep the same state — only the pending mark changes.
  [Fact]
  public async Task AConfirmingReportClearsPendingWithoutASecondTransition()
  {
    var factory = new FakeFactory { Preloaded = State(DeviceIoKind.Relay, "relay1", false) };
    await using var service = Service(factory);
    var events = Capture(service);
    await Report(service, DeviceIoKind.Relay, [Relay("relay1")]);
    events.Clear();

    await Command(service, true);
    events.Clear();
    factory.Created[0].Report(State(DeviceIoKind.Relay, "relay1", true));

    var confirmed = Assert.Single(events);
    Assert.True(confirmed.Active);
    Assert.False(confirmed.Initialized);
    Assert.True(service.TryGetLogicalState(CameraId, DeviceIoKind.Relay, "relay1"));
    Assert.False(service.TryGetSnapshot(CameraId, DeviceIoKind.Relay, "relay1")?.Pending);
  }

  // The subscription delivers on its own schedule, so a report can be older than the command that
  // followed it. It must not undo the wish: that is what turned "off" back into "on".
  [Fact]
  public async Task AReportWithTheOldValueDoesNotUndoTheWish()
  {
    var factory = new FakeFactory { Preloaded = State(DeviceIoKind.Relay, "relay1", true) };
    await using var service = Service(factory);
    var events = Capture(service);
    await Report(service, DeviceIoKind.Relay, [Relay("relay1")]);
    events.Clear();

    await Command(service, false);
    events.Clear();
    // The camera's event for the previous "on" arrives after the command to switch it off.
    factory.Created[0].Report(State(DeviceIoKind.Relay, "relay1", true));
    await WaitForDebounceAsync();

    Assert.Empty(events);
    Assert.False(service.TryGetLogicalState(CameraId, DeviceIoKind.Relay, "relay1"));
    Assert.True(service.TryGetSnapshot(CameraId, DeviceIoKind.Relay, "relay1")?.Pending);
  }

  [Fact]
  public async Task TheRetrySweepRepeatsAnUnconfirmedCommand()
  {
    var commander = new FakeCommander();
    var time = new FakeTime();
    var factory = new FakeFactory { Preloaded = State(DeviceIoKind.Relay, "relay1", false) };
    await using var service = Service(factory, commander: commander, time: time);
    await Report(service, DeviceIoKind.Relay, [Relay("relay1")]);
    await Command(service, true);
    Assert.Empty(commander.Sent);

    // Not yet due.
    await service.RetryPendingAsync(CancellationToken.None);
    Assert.Empty(commander.Sent);

    time.Advance(DeviceIoStateService.RetryInterval + TimeSpan.FromMilliseconds(100));
    await service.RetryPendingAsync(CancellationToken.None);
    Assert.Equal([(CameraId, "relay1", true)], commander.Sent);

    time.Advance(DeviceIoStateService.RetryInterval);
    await service.RetryPendingAsync(CancellationToken.None);
    Assert.Equal(2, commander.Sent.Count);
  }

  [Fact]
  public async Task AnUnconfirmedCommandIsGivenUpAfterItsLifetime()
  {
    var commander = new FakeCommander();
    var time = new FakeTime();
    var factory = new FakeFactory { Preloaded = State(DeviceIoKind.Relay, "relay1", false) };
    await using var service = Service(factory, commander: commander, time: time);
    var events = Capture(service);
    await Report(service, DeviceIoKind.Relay, [Relay("relay1")]);
    events.Clear();

    await Command(service, true);
    events.Clear();

    time.Advance(DeviceIoStateService.ConfirmTimeout + TimeSpan.FromSeconds(1));
    await service.RetryPendingAsync(CancellationToken.None);

    // The camera's own state comes back, and the screens stop showing a wish that never came true.
    var givenUp = Assert.Single(events);
    Assert.False(givenUp.Active);
    Assert.False(service.TryGetLogicalState(CameraId, DeviceIoKind.Relay, "relay1"));
    Assert.False(service.TryGetSnapshot(CameraId, DeviceIoKind.Relay, "relay1")?.Pending);
  }

  // A relay the camera has never reported has no report to confirm against, but the wish is still
  // held: the subscription this command just opened replays a snapshot taken *before* the command,
  // and publishing the command once would let that snapshot undo it. The commanded value is what a
  // camera that never speaks leaves behind when the wish expires — no rollback.
  [Fact]
  public async Task ACommandForARelayWithoutABaselineIsStillHeld()
  {
    var commander = new FakeCommander();
    var time = new FakeTime();
    var factory = new FakeFactory();
    await using var service = Service(factory, commander: commander, time: time);
    var events = Capture(service);
    await Report(service, DeviceIoKind.Relay, [Relay("relay1")]);
    events.Clear();

    await Command(service, true);

    var raised = Assert.Single(events);
    Assert.True(raised.Active);
    Assert.True(service.TryGetSnapshot(CameraId, DeviceIoKind.Relay, "relay1")?.Pending);

    time.Advance(DeviceIoStateService.RetryInterval + TimeSpan.FromMilliseconds(100));
    await service.RetryPendingAsync(CancellationToken.None);
    Assert.Equal([(CameraId, "relay1", true)], commander.Sent);

    // Nobody ever confirmed it; after the lifetime the wish goes, and the commanded value is what
    // is left — the camera having said nothing is not evidence that the command failed.
    events.Clear();
    time.Advance(DeviceIoStateService.ConfirmTimeout);
    await service.RetryPendingAsync(CancellationToken.None);

    var cleared = Assert.Single(events);
    Assert.True(cleared.Active);
    Assert.True(service.TryGetLogicalState(CameraId, DeviceIoKind.Relay, "relay1"));
    Assert.False(service.TryGetSnapshot(CameraId, DeviceIoKind.Relay, "relay1")?.Pending);
  }

  // A monostable relay drops on the camera's own clock. The drop is not a refusal to obey: the
  // wish lives past it, and only when it expires does the camera's own state go back on screen.
  [Fact]
  public async Task AMonostableRelaysOwnDropIsNotARefusal()
  {
    var time = new FakeTime();
    var factory = new FakeFactory { Preloaded = State(DeviceIoKind.Relay, "relay1", false) };
    await using var service = Service(factory, time: time);
    var events = Capture(service);
    await Report(service, DeviceIoKind.Relay, [Relay("relay1")]);
    events.Clear();

    var lifetime = TimeSpan.FromSeconds(5) + DeviceIoStateService.MonostableGrace;
    await service.SetDesiredStateAsync(
      CameraId, DeviceIoKind.Relay, "relay1", true, lifetime);
    events.Clear();

    // The camera drops the coil by itself and reports "inactive"; the wish still stands.
    factory.Created[0].Report(State(DeviceIoKind.Relay, "relay1", false));
    await WaitForDebounceAsync();
    Assert.Empty(events);
    Assert.True(service.TryGetLogicalState(CameraId, DeviceIoKind.Relay, "relay1"));

    time.Advance(lifetime + TimeSpan.FromMilliseconds(100));
    await service.RetryPendingAsync(CancellationToken.None);

    var dropped = Assert.Single(events);
    Assert.False(dropped.Active);
    Assert.False(service.TryGetLogicalState(CameraId, DeviceIoKind.Relay, "relay1"));
  }

  // A relay the subscription has not reported is not confirmed against anything; its first
  // Initialized report still brings it in as the camera says.
  [Fact]
  public async Task ACommandForAnUnknownRelayIsNotKept()
  {
    var factory = new FakeFactory();
    await using var service = Service(factory);
    var events = Capture(service);
    await Report(service, DeviceIoKind.Relay, [Relay("relay1")]);
    events.Clear();

    await service.SetDesiredStateAsync(
      CameraId, DeviceIoKind.Relay, "relay9", true, DeviceIoStateService.ConfirmTimeout);

    Assert.Empty(events);
    Assert.Null(service.TryGetLogicalState(CameraId, DeviceIoKind.Relay, "relay9"));
  }

  private static Task Command(DeviceIoStateService service, bool active) =>
    service.SetDesiredStateAsync(
      CameraId, DeviceIoKind.Relay, "relay1", active, DeviceIoStateService.ConfirmTimeout);

  private static ConcurrentQueue<DeviceEvent> Capture(DeviceIoStateService service)
  {
    var events = new ConcurrentQueue<DeviceEvent>();
    service.DeviceEventRaised += events.Enqueue;
    return events;
  }

  private static DeviceIoState State(
    DeviceIoKind kind, string token, bool logical, string operation = "Changed") =>
    new(kind, token, logical, kind == DeviceIoKind.Relay ? logical : null, operation, null);

  private sealed class FakeCommander : IRelayCommander
  {
    public List<(string CameraId, string Token, bool Active)> Sent { get; } = [];

    public Task SetRelayOutputStateAsync(string cameraId, string token, bool active, CancellationToken ct)
    {
      Sent.Add((cameraId, token, active));
      return Task.CompletedTask;
    }
  }

  /// <summary>A clock the test moves by hand, so retry intervals cost no real time.</summary>
  private sealed class FakeTime : TimeProvider
  {
    private DateTimeOffset _now = new(2026, 11, 12, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
  }

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
