using System.Text.Json;
using VmsSingleton.EventRouting;

namespace VmsSingleton.Tests;

// Shared builders and the in-memory doubles the router tests run against. The doubles are the
// seams the router was written around: the journal, the node's own services, the configuration
// list and the state store.
internal static class RoutingTestData
{
  public const string CameraA = "00000000-0000-0000-0000-0000000000a1";
  public const string CameraB = "00000000-0000-0000-0000-0000000000b2";
  public const string SubscriptionId = "00000000-0000-0000-0000-0000000000c3";
  public const string TriggerId = "00000000-0000-0000-0000-0000000000d4";

  public static EventFacts Event(
    string? id = null,
    string objectId = "00000000-0000-0000-0000-0000000000e5",
    string eventName = "sensor",
    string param0 = "closed",
    Dictionary<string, string>? props = null)
  {
    var values = new Dictionary<string, string>(StringComparer.Ordinal)
    {
      ["camera_id"] = CameraA,
      ["part_kind"] = "digital_input",
      ["token"] = "input1",
      ["contact"] = "closed",
    };

    if (props is not null)
    {
      foreach (var (key, value) in props)
        values[key] = value;
    }

    return new EventFacts(id ?? TriggerId, objectId, eventName, param0, string.Empty, values);
  }

  public static SubscriptionSnapshot Subscription(
    string eventName = "sensor",
    EventSelector? selector = null,
    string armedCondition = SubscriptionMatcher.ArmedConditionAny,
    int cooldownMs = 0,
    int version = 1,
    IReadOnlyList<ActionSpec>? actions = null,
    string? id = null) =>
    new(
      Id: id ?? SubscriptionId,
      Name: "test subscription",
      EventName: eventName,
      Selector: selector ?? new EventSelector([], [], [], [], new Dictionary<string, string>(), null),
      ArmedCondition: armedCondition,
      CooldownMs: cooldownMs,
      Version: version,
      Actions: actions ?? [RetainAction(CameraA)]);

  public static ActionSpec RetainAction(string camera, int position = 0, string? eventId = null)
  {
    var json = eventId is null
      ? $$"""{"cameras":["{{camera}}"],"timeout_seconds":30}"""
      : $$"""{"cameras":["{{camera}}"],"timeout_seconds":30,"event_id":"{{eventId}}"}""";
    return new ActionSpec(position, Routing.ActionRetainRecording, Json(json));
  }

  public static ActionSpec RelayAction(string camera = CameraA, int position = 0) =>
    new(position, Routing.ActionSetRelay,
      Json($$"""{"camera_id":"{{camera}}","token":"lamp","active":true}"""));

  public static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();
}

internal sealed class FakeMapGateway : IMapGateway
{
  public EventFacts? Event { get; set; } = RoutingTestData.Event();
  public bool? Armed { get; set; } = true;
  public List<EmitEventCommand> Emitted { get; } = [];
  public List<RaiseAlarmCommand> Raised { get; } = [];

  public Task<EventFacts?> GetEventAsync(Guid id, CancellationToken ct) =>
    Task.FromResult(Event is not null && Guid.Parse(Event.Id) == id ? Event : null);

  public Task<bool?> GetArmedAsync(string objectId, CancellationToken ct) =>
    Task.FromResult(Armed);

  public Task EmitEventAsync(EmitEventCommand command, CancellationToken ct)
  {
    Emitted.Add(command);
    return Task.CompletedTask;
  }

  public Task RaiseAlarmAsync(RaiseAlarmCommand command, CancellationToken ct)
  {
    Raised.Add(command);
    return Task.CompletedTask;
  }
}

internal sealed class FakeNodeActions : INodeActions
{
  public List<(string CameraId, RetainRecordingCommand Command)> Retained { get; } = [];
  public List<(string CameraId, string Token, bool Active)> Relays { get; } = [];

  /// <summary>When set, the next call of that kind throws once and clears itself.</summary>
  public string? FailNext { get; set; }
  public bool AlwaysFail { get; set; }

  public Task RetainRecordingAsync(
    string cameraId, RetainRecordingCommand command, CancellationToken ct)
  {
    ThrowIfFailing("retain");
    Retained.Add((cameraId, command));
    return Task.CompletedTask;
  }

  public Task SetRelayAsync(string cameraId, string token, bool active, CancellationToken ct)
  {
    ThrowIfFailing("relay");
    Relays.Add((cameraId, token, active));
    return Task.CompletedTask;
  }

  private void ThrowIfFailing(string kind)
  {
    if (AlwaysFail)
      throw new InvalidOperationException($"{kind} unavailable");

    if (FailNext == kind)
    {
      FailNext = null;
      throw new InvalidOperationException($"{kind} unavailable");
    }
  }
}

internal sealed class FakeRunStore : IRunStore
{
  private readonly Dictionary<string, string> _runs = new(StringComparer.Ordinal);
  private readonly Dictionary<string, DateTime> _cooldowns = new(StringComparer.Ordinal);

  public IReadOnlyCollection<string> RunKeys => _runs.Keys;

  /// <summary>A serialized round trip, like the state store: a resume reads what was written.</summary>
  public Task<EventRun?> GetAsync(string subscriptionId, string triggerEventId, CancellationToken ct) =>
    Task.FromResult(
      _runs.TryGetValue(EventRun.Key(subscriptionId, triggerEventId), out var json)
        ? JsonSerializer.Deserialize<EventRun>(json)
        : null);

  public Task SaveAsync(EventRun run, CancellationToken ct)
  {
    _runs[EventRun.Key(run.SubscriptionId, run.TriggerEventId)] = JsonSerializer.Serialize(run);
    return Task.CompletedTask;
  }

  public Task<bool> IsCoolingDownAsync(
    string subscriptionId, string sourceObjectId, TimeSpan cooldown, CancellationToken ct) =>
    Task.FromResult(
      cooldown > TimeSpan.Zero
      && _cooldowns.TryGetValue(Key(subscriptionId, sourceObjectId), out var at)
      && DateTime.UtcNow - at < cooldown);

  public Task StartCooldownAsync(
    string subscriptionId, string sourceObjectId, TimeSpan cooldown, CancellationToken ct)
  {
    if (cooldown > TimeSpan.Zero)
      _cooldowns[Key(subscriptionId, sourceObjectId)] = DateTime.UtcNow;
    return Task.CompletedTask;
  }

  private static string Key(string subscriptionId, string objectId) =>
    $"{subscriptionId}:{objectId}";
}

internal sealed class FakeSubscriptions(params SubscriptionSnapshot[] items) : ISubscriptionSource
{
  public List<SubscriptionSnapshot> Items { get; } = [.. items];

  public Task<IReadOnlyList<SubscriptionSnapshot>> ListAsync(CancellationToken ct) =>
    Task.FromResult<IReadOnlyList<SubscriptionSnapshot>>(Items);

  public void Invalidate() { }
}

internal sealed class FakeDiagnostics : IRoutingDiagnostics
{
  public List<(string EntityType, string EntityId, object State)> Reports { get; } = [];

  public void Report(string entityType, string entityId, object state) =>
    Reports.Add((entityType, entityId, state));
}
