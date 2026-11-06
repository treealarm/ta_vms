using System.Text.Json;
using System.Text.Json.Serialization;

namespace VmsSingleton.EventRouting;

/// <summary>Names and limits the whole router shares.</summary>
public static class Routing
{
  /// <summary>VmsMap's outbox announces a journal row here: <c>{"event_id": "..."}</c>.</summary>
  public const string EventCreatedTopic = "map-event-created";

  /// <summary>VmsCfg's signal that the subscriptions or their definitions changed.</summary>
  public const string SubscriptionsChangedTopic = "event-subscriptions-changed";

  /// <summary>The debug registry's entity types for the two things an operator can see failing.</summary>
  public const string RunEntityType = "event_run";
  public const string SuppressedEntityType = "event_routing_suppressed";

  /// <summary>
  /// How deep a chain of subscription-emitted events may go. A model guard, not a general cycle
  /// detector: the same subscription firing twice in one chain is refused outright, and this caps
  /// the chain of different ones.
  /// </summary>
  public const int MaxDepth = 8;

  /// <summary>Deliveries one run gets before its failure becomes final and the message is acked.</summary>
  public const int MaxAttempts = 5;

  /// <summary>How long a run record survives in the state store.</summary>
  public static readonly TimeSpan RunTtl = TimeSpan.FromHours(24);

  // The journal names the router reads. They mirror VmsMap's dictionary; VmsCfg validates the
  // operator's choice against the same set.
  public const string EventSensor = "sensor";
  public const string EventRelay = "relay";
  public const string EventAnalytics = "analytics";

  // Extra-prop names of a subscription-emitted event: the chain that produced it. They are props
  // rather than a second table because the journal row is what the next hop reads.
  public const string PropCausationId = "causation_id";
  public const string PropOrigin = "origin";
  public const string PropDepth = "depth";
  public const string PropCausationChain = "causation_chain";
  public const string PropSubscriptionId = "subscription_id";

  public const string OriginSubscription = "subscription";

  // Action kinds, as VmsCfg's CHECK constraint spells them.
  public const string ActionRetainRecording = "retain_recording";
  public const string ActionSetRelay = "set_relay";
  public const string ActionEmitEvent = "emit_event";
  public const string ActionRaiseAlarm = "raise_alarm";

  /// <summary>"This event's own node" in an action parameter.</summary>
  public const string SourceObject = "source";

  /// <summary>"The camera whose part produced the event" in a retention action.</summary>
  public const string SourceCamera = "source_camera";

  /// <summary>The episode key a retention action uses when its own template is absent.</summary>
  public const string DefaultRetentionEventId = "subscription:{subscription_id}:{source_object_id}";

  private static readonly System.Text.RegularExpressions.Regex Placeholder =
    new(@"\{([a-z_]+)\}", System.Text.RegularExpressions.RegexOptions.Compiled);

  /// <summary>
  /// Fills an action's template with the run's identifiers. VmsCfg rejects unknown placeholders
  /// when the subscription is saved, so an unknown one here is left as written rather than failed:
  /// a wrong-looking key is more useful than a run that stopped.
  /// </summary>
  public static string ExpandTemplate(
    string? template, string subscriptionId, string sourceObjectId, string? cameraId) =>
    template is null
      ? string.Empty
      : Placeholder.Replace(template, match => match.Groups[1].Value switch
      {
        "subscription_id" => subscriptionId,
        "source_object_id" => sourceObjectId,
        "camera_id" => cameraId ?? string.Empty,
        _ => match.Value,
      });
}

/// <summary>
/// One journal row as the router sees it: the fields and extra props, nothing else. `camera_id`,
/// `part_kind`, `token` and the contact live in the props because the router has no device_node.
/// </summary>
public sealed record EventFacts(
  string Id,
  string ObjectId,
  string EventName,
  string Param0,
  string Param1,
  IReadOnlyDictionary<string, string> Props)
{
  public string? Prop(string name) => Props.TryGetValue(name, out var value) ? value : null;
}

/// <summary>
/// The source narrowing of a subscription. Every list is an OR inside itself and an AND with the
/// others; an empty list or an absent field is "any". A field the event does not carry never
/// matches a non-empty list — "no camera_id" is not "any camera".
/// </summary>
public sealed record EventSelector(
  IReadOnlyList<string> ObjectIds,
  IReadOnlyList<string> CameraIds,
  IReadOnlyList<string> Kinds,
  IReadOnlyList<string> Tokens,
  IReadOnlyDictionary<string, string> Props,
  double? MinConfidence);

/// <summary>One action of a subscription, with its JSON parameters as VmsCfg stored them.</summary>
public sealed record ActionSpec(int Position, string Kind, JsonElement Params);

/// <summary>
/// A subscription as a run sees it. The router keeps a copy of the whole list, and a run keeps the
/// copy it started with: editing or disabling a subscription never rewrites work already under way.
/// </summary>
public sealed record SubscriptionSnapshot(
  string Id,
  string Name,
  string EventName,
  EventSelector Selector,
  string ArmedCondition,
  int CooldownMs,
  int Version,
  IReadOnlyList<ActionSpec> Actions);

/// <summary>What a journal event asks the node's own services to do.</summary>
public sealed record RetainRecordingCommand(
  string EventId,
  int TimeoutSeconds,
  bool RaiseAlarm,
  DateTime AlarmStartTs);

public sealed record EmitEventCommand(
  Guid Id,
  string ObjectId,
  string EventName,
  string Param0,
  string Param1,
  IReadOnlyDictionary<string, string> Props);

public sealed record RaiseAlarmCommand(
  Guid ObjectId,
  string Source,
  string DedupKey,
  Guid SubscriptionId,
  Guid SourceEventId);

/// <summary>One run's progress, as the state store keeps it.</summary>
public sealed class EventRun
{
  public const string Running = "running";
  public const string Completed = "completed";
  public const string Failed = "failed";

  [JsonPropertyName("subscription_id")]
  public string SubscriptionId { get; set; } = string.Empty;

  [JsonPropertyName("trigger_event_id")]
  public string TriggerEventId { get; set; } = string.Empty;

  [JsonPropertyName("version")]
  public int Version { get; set; }

  /// <summary>The subscription as it was when the run started: an edit never rewrites a run.</summary>
  [JsonPropertyName("snapshot")]
  public SubscriptionSnapshot? Snapshot { get; set; }

  [JsonPropertyName("next_position")]
  public int NextPosition { get; set; }

  [JsonPropertyName("status")]
  public string Status { get; set; } = Running;

  [JsonPropertyName("attempts")]
  public int Attempts { get; set; }

  [JsonPropertyName("error")]
  public string? Error { get; set; }

  [JsonPropertyName("updated_at")]
  public DateTime UpdatedAt { get; set; }

  public static string Key(string subscriptionId, string triggerEventId) =>
    $"event-run:{subscriptionId}:{triggerEventId}";
}

/// <summary>Where a run's progress and the subscription cooldowns live.</summary>
public interface IRunStore
{
  Task<EventRun?> GetAsync(string subscriptionId, string triggerEventId, CancellationToken ct);
  Task SaveAsync(EventRun run, CancellationToken ct);

  /// <summary>Whether an earlier event of this source fired the subscription too recently.</summary>
  Task<bool> IsCoolingDownAsync(
    string subscriptionId, string sourceObjectId, TimeSpan cooldown, CancellationToken ct);

  /// <summary>
  /// Starts a new cooldown. Called only after the run record is written: the run is what a repeat
  /// resumes, so it must exist before anything that could make the repeat look like a storm.
  /// </summary>
  Task StartCooldownAsync(
    string subscriptionId, string sourceObjectId, TimeSpan cooldown, CancellationToken ct);
}

/// <summary>Where the router reads its configuration from, with the cache in front of it.</summary>
public interface ISubscriptionSource
{
  Task<IReadOnlyList<SubscriptionSnapshot>> ListAsync(CancellationToken ct);
  void Invalidate();
}

/// <summary>What the router needs from the journal and the alarm entity.</summary>
public interface IMapGateway
{
  /// <summary>The event, or null when the journal no longer has it (its camera was deleted).</summary>
  Task<EventFacts?> GetEventAsync(Guid id, CancellationToken ct);

  /// <summary>Whether the node is armed; null when it is not a device node at all.</summary>
  Task<bool?> GetArmedAsync(string objectId, CancellationToken ct);

  Task EmitEventAsync(EmitEventCommand command, CancellationToken ct);
  Task RaiseAlarmAsync(RaiseAlarmCommand command, CancellationToken ct);
}

/// <summary>What the router asks a camera's own node to do.</summary>
public interface INodeActions
{
  Task RetainRecordingAsync(string cameraId, RetainRecordingCommand command, CancellationToken ct);
  Task SetRelayAsync(string cameraId, string token, bool active, CancellationToken ct);
}

/// <summary>The best-effort debug registry an operator reads a failed or suppressed run from.</summary>
public interface IRoutingDiagnostics
{
  void Report(string entityType, string entityId, object state);
}
