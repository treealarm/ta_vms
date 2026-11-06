namespace VmsSingleton.Tests;

using Microsoft.Extensions.Logging.Abstractions;
using VmsSingleton.EventRouting;

public class EventRouterTests
{
  private static EventRouter Router(
    FakeSubscriptions subscriptions,
    FakeMapGateway map,
    FakeNodeActions nodes,
    FakeRunStore runs,
    FakeDiagnostics diagnostics) =>
    new(subscriptions, map, new ActionExecutor(map, nodes), runs, diagnostics,
      NullLogger<EventRouter>.Instance);

  [Fact]
  public async Task ARepeatedDeliveryDoesNotRunTheSubscriptionTwice()
  {
    var subscriptions = new FakeSubscriptions(RoutingTestData.Subscription());
    var map = new FakeMapGateway();
    var nodes = new FakeNodeActions();
    var runs = new FakeRunStore();
    var router = Router(subscriptions, map, nodes, runs, new FakeDiagnostics());

    Assert.Equal(RouteResult.Ack, await router.HandleAsync(Guid.Parse(RoutingTestData.TriggerId), default));
    Assert.Equal(RouteResult.Ack, await router.HandleAsync(Guid.Parse(RoutingTestData.TriggerId), default));

    Assert.Single(nodes.Retained);

    var run = await runs.GetAsync(
      RoutingTestData.SubscriptionId, RoutingTestData.TriggerId, default);
    Assert.Equal(EventRun.Completed, run!.Status);
    Assert.Equal(1, run.NextPosition);
  }

  [Fact]
  public async Task AFailureOnTheSecondOfThreeActionsResumesAtIt()
  {
    var subscription = RoutingTestData.Subscription(actions:
    [
      RoutingTestData.RetainAction(RoutingTestData.CameraA, position: 0),
      RoutingTestData.RelayAction(position: 1),
      RoutingTestData.RetainAction(RoutingTestData.CameraB, position: 2),
    ]);

    var subscriptions = new FakeSubscriptions(subscription);
    var map = new FakeMapGateway();
    var nodes = new FakeNodeActions { FailNext = "relay" };
    var runs = new FakeRunStore();
    var router = Router(subscriptions, map, nodes, runs, new FakeDiagnostics());

    var eventId = Guid.Parse(RoutingTestData.TriggerId);

    Assert.Equal(RouteResult.Retry, await router.HandleAsync(eventId, default));
    Assert.Single(nodes.Retained);
    Assert.Equal(RoutingTestData.CameraA, nodes.Retained[0].CameraId);

    var interrupted = await runs.GetAsync(
      RoutingTestData.SubscriptionId, RoutingTestData.TriggerId, default);
    Assert.Equal(EventRun.Running, interrupted!.Status);
    Assert.Equal(1, interrupted.NextPosition);

    Assert.Equal(RouteResult.Ack, await router.HandleAsync(eventId, default));

    // The first action is not repeated; the relay and the second retention ran.
    Assert.Equal(2, nodes.Retained.Count);
    Assert.Equal(RoutingTestData.CameraB, nodes.Retained[1].CameraId);
    Assert.Single(nodes.Relays);

    var completed = await runs.GetAsync(
      RoutingTestData.SubscriptionId, RoutingTestData.TriggerId, default);
    Assert.Equal(EventRun.Completed, completed!.Status);
    Assert.Equal(2, completed.Attempts);
  }

  [Fact]
  public async Task ACooldownSwallowsASecondEventFromTheSameSource()
  {
    var subscriptions = new FakeSubscriptions(RoutingTestData.Subscription(cooldownMs: 60_000));
    var map = new FakeMapGateway();
    var nodes = new FakeNodeActions();
    var router = Router(subscriptions, map, nodes, new FakeRunStore(), new FakeDiagnostics());

    Assert.Equal(RouteResult.Ack,
      await router.HandleAsync(Guid.Parse(RoutingTestData.TriggerId), default));

    map.Event = RoutingTestData.Event(id: "00000000-0000-0000-0000-0000000000f6");
    Assert.Equal(RouteResult.Ack,
      await router.HandleAsync(Guid.Parse("00000000-0000-0000-0000-0000000000f6"), default));

    Assert.Single(nodes.Retained);
  }

  [Fact]
  public async Task ACooldownDoesNotBlockResumingAFailedRun()
  {
    // The regression the plan's flow order invited: a cooldown taken by the run itself must not
    // look like a storm to the retry of that same run.
    var subscriptions = new FakeSubscriptions(RoutingTestData.Subscription(cooldownMs: 60_000));
    var map = new FakeMapGateway();
    var nodes = new FakeNodeActions { FailNext = "retain" };
    var runs = new FakeRunStore();
    var router = Router(subscriptions, map, nodes, runs, new FakeDiagnostics());

    var eventId = Guid.Parse(RoutingTestData.TriggerId);

    Assert.Equal(RouteResult.Retry, await router.HandleAsync(eventId, default));
    Assert.Empty(nodes.Retained);

    Assert.Equal(RouteResult.Ack, await router.HandleAsync(eventId, default));
    Assert.Single(nodes.Retained);
  }

  [Fact]
  public async Task ARunKeepsTheSnapshotItStartedFrom()
  {
    var first = RoutingTestData.Subscription(version: 1, actions:
    [
      RoutingTestData.RetainAction(RoutingTestData.CameraA, position: 0),
      RoutingTestData.RelayAction(position: 1),
    ]);

    var subscriptions = new FakeSubscriptions(first);
    var map = new FakeMapGateway();
    var nodes = new FakeNodeActions { FailNext = "relay" };
    var runs = new FakeRunStore();
    var router = Router(subscriptions, map, nodes, runs, new FakeDiagnostics());

    var eventId = Guid.Parse(RoutingTestData.TriggerId);
    Assert.Equal(RouteResult.Retry, await router.HandleAsync(eventId, default));

    // The operator edits the subscription between the delivery and its repeat.
    subscriptions.Items[0] = RoutingTestData.Subscription(version: 2, actions:
    [
      RoutingTestData.RetainAction(RoutingTestData.CameraB, position: 0),
    ]);

    Assert.Equal(RouteResult.Ack, await router.HandleAsync(eventId, default));

    // The old snapshot finished: the relay ran, and nothing named the new action.
    Assert.Single(nodes.Relays);
    Assert.Single(nodes.Retained);
    Assert.Equal(RoutingTestData.CameraA, nodes.Retained[0].CameraId);

    var run = await runs.GetAsync(RoutingTestData.SubscriptionId, RoutingTestData.TriggerId, default);
    Assert.Equal(EventRun.Completed, run!.Status);
    Assert.Equal(1, run.Version);
  }

  [Fact]
  public async Task OneRetentionActionOnTwoCamerasLeavesTwoEpisodesUnderOneKey()
  {
    var parameters = RoutingTestData.Json(
      $$"""{"cameras":["{{RoutingTestData.CameraA}}","{{RoutingTestData.CameraB}}"],"timeout_seconds":60}""");

    var subscriptions = new FakeSubscriptions(RoutingTestData.Subscription(actions:
      [new ActionSpec(0, Routing.ActionRetainRecording, parameters)]));
    var map = new FakeMapGateway();
    var nodes = new FakeNodeActions();
    var router = Router(subscriptions, map, nodes, new FakeRunStore(), new FakeDiagnostics());

    await router.HandleAsync(Guid.Parse(RoutingTestData.TriggerId), default);

    Assert.Equal(2, nodes.Retained.Count);
    Assert.Equal(
      [RoutingTestData.CameraA, RoutingTestData.CameraB],
      nodes.Retained.Select(x => x.CameraId));
    Assert.Equal(
      $"subscription:{RoutingTestData.SubscriptionId}:{RoutingTestData.Event().ObjectId}",
      nodes.Retained[0].Command.EventId);
    Assert.All(nodes.Retained, x => Assert.Equal(nodes.Retained[0].Command.EventId, x.Command.EventId));
  }

  [Fact]
  public async Task FiveFailedAttemptsEndTheRunAndReportIt()
  {
    var subscriptions = new FakeSubscriptions(RoutingTestData.Subscription());
    var map = new FakeMapGateway();
    var nodes = new FakeNodeActions { AlwaysFail = true };
    var runs = new FakeRunStore();
    var diagnostics = new FakeDiagnostics();
    var router = Router(subscriptions, map, nodes, runs, diagnostics);

    var eventId = Guid.Parse(RoutingTestData.TriggerId);

    for (var attempt = 1; attempt < Routing.MaxAttempts; attempt++)
      Assert.Equal(RouteResult.Retry, await router.HandleAsync(eventId, default));

    Assert.Equal(RouteResult.Ack, await router.HandleAsync(eventId, default));

    var run = await runs.GetAsync(RoutingTestData.SubscriptionId, RoutingTestData.TriggerId, default);
    Assert.Equal(EventRun.Failed, run!.Status);
    Assert.Equal(Routing.MaxAttempts, run.Attempts);
    Assert.NotNull(run.Error);

    var report = Assert.Single(diagnostics.Reports);
    Assert.Equal(Routing.RunEntityType, report.EntityType);

    // A failed run is not redelivered again.
    Assert.Equal(RouteResult.Ack, await router.HandleAsync(eventId, default));
  }

  [Fact]
  public async Task ASubscriptionAlreadyOnTheChainIsSuppressedAndReported()
  {
    var subscriptions = new FakeSubscriptions(RoutingTestData.Subscription());
    var map = new FakeMapGateway
    {
      Event = RoutingTestData.Event(props: new Dictionary<string, string>
      {
        [Routing.PropDepth] = "1",
        [Routing.PropCausationChain] = RoutingTestData.SubscriptionId,
      }),
    };
    var nodes = new FakeNodeActions();
    var diagnostics = new FakeDiagnostics();
    var router = Router(subscriptions, map, nodes, new FakeRunStore(), diagnostics);

    Assert.Equal(RouteResult.Ack,
      await router.HandleAsync(Guid.Parse(RoutingTestData.TriggerId), default));

    Assert.Empty(nodes.Retained);
    var report = Assert.Single(diagnostics.Reports);
    Assert.Equal(Routing.SuppressedEntityType, report.EntityType);
  }

  [Fact]
  public async Task AnEventThatLeftTheJournalIsAcknowledged()
  {
    var subscriptions = new FakeSubscriptions(RoutingTestData.Subscription());
    var map = new FakeMapGateway { Event = null };
    var nodes = new FakeNodeActions();
    var router = Router(subscriptions, map, nodes, new FakeRunStore(), new FakeDiagnostics());

    Assert.Equal(RouteResult.Ack,
      await router.HandleAsync(Guid.Parse(RoutingTestData.TriggerId), default));

    Assert.Empty(nodes.Retained);
  }

  [Fact]
  public async Task AnArmedConditionUsesTheArmsTheMapAnswers()
  {
    var subscription = RoutingTestData.Subscription(armedCondition: SubscriptionMatcher.ArmedConditionArmed);
    var subscriptions = new FakeSubscriptions(subscription);
    var map = new FakeMapGateway { Armed = false };
    var nodes = new FakeNodeActions();
    var router = Router(subscriptions, map, nodes, new FakeRunStore(), new FakeDiagnostics());

    var eventId = Guid.Parse(RoutingTestData.TriggerId);
    Assert.Equal(RouteResult.Ack, await router.HandleAsync(eventId, default));
    Assert.Empty(nodes.Retained);

    map.Armed = true;
    Assert.Equal(RouteResult.Ack, await router.HandleAsync(eventId, default));
    Assert.Single(nodes.Retained);
  }

  [Fact]
  public async Task AnEmittedEventCarriesTheChainAndAnIdDerivedFromTheAction()
  {
    var parameters = RoutingTestData.Json(
      "{\"object_id\":\"source\",\"event_name\":\"relay\",\"param0\":\"active\",\"props\":{\"custom\":\"x\"}}");

    var subscriptions = new FakeSubscriptions(RoutingTestData.Subscription(actions:
      [new ActionSpec(0, Routing.ActionEmitEvent, parameters)]));
    var map = new FakeMapGateway();
    var router = Router(subscriptions, map, new FakeNodeActions(), new FakeRunStore(), new FakeDiagnostics());

    var eventId = Guid.Parse(RoutingTestData.TriggerId);
    Assert.Equal(RouteResult.Ack, await router.HandleAsync(eventId, default));
    Assert.Equal(RouteResult.Ack, await router.HandleAsync(eventId, default));

    var emitted = Assert.Single(map.Emitted);
    Assert.Equal(RoutingTestData.Event().ObjectId, emitted.ObjectId);
    Assert.Equal(RoutingTestData.TriggerId, emitted.Props[Routing.PropCausationId]);
    Assert.Equal(Routing.OriginSubscription, emitted.Props[Routing.PropOrigin]);
    Assert.Equal("1", emitted.Props[Routing.PropDepth]);
    Assert.Equal(RoutingTestData.SubscriptionId, emitted.Props[Routing.PropCausationChain]);
    Assert.Equal("x", emitted.Props["custom"]);
  }

  [Fact]
  public async Task ARaiseCarriesTheOperationKeyAndTheSubscription()
  {
    var parameters = RoutingTestData.Json(
      $$"""{"object_id":"source","definition":"subscription","dedup_key":"sub:{subscription_id}"}""");

    var subscriptions = new FakeSubscriptions(RoutingTestData.Subscription(actions:
      [new ActionSpec(0, Routing.ActionRaiseAlarm, parameters)]));
    var map = new FakeMapGateway();
    var router = Router(subscriptions, map, new FakeNodeActions(), new FakeRunStore(), new FakeDiagnostics());

    await router.HandleAsync(Guid.Parse(RoutingTestData.TriggerId), default);

    var raised = Assert.Single(map.Raised);
    Assert.Equal(Guid.Parse(RoutingTestData.Event().ObjectId), raised.ObjectId);
    Assert.Equal("subscription", raised.Source);
    Assert.Equal($"sub:{RoutingTestData.SubscriptionId}", raised.DedupKey);
    Assert.Equal(Guid.Parse(RoutingTestData.SubscriptionId), raised.SubscriptionId);
    Assert.Equal(Guid.Parse(RoutingTestData.TriggerId), raised.SourceEventId);
  }

  [Fact]
  public async Task TheChainCannotBeForgedThroughTheActionsOwnProps()
  {
    var parameters = RoutingTestData.Json(
      "{\"object_id\":\"source\",\"event_name\":\"relay\",\"props\":{\"depth\":\"99\",\"causation_chain\":\"someone\"}}");

    var subscriptions = new FakeSubscriptions(RoutingTestData.Subscription(actions:
      [new ActionSpec(0, Routing.ActionEmitEvent, parameters)]));
    var map = new FakeMapGateway();
    var router = Router(subscriptions, map, new FakeNodeActions(), new FakeRunStore(), new FakeDiagnostics());

    await router.HandleAsync(Guid.Parse(RoutingTestData.TriggerId), default);

    var emitted = Assert.Single(map.Emitted);
    Assert.Equal("1", emitted.Props[Routing.PropDepth]);
    Assert.Equal(RoutingTestData.SubscriptionId, emitted.Props[Routing.PropCausationChain]);
  }
}
