using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VmsSingleton.EventRouting;

/// <summary>
/// Runs one action of a started subscription. Every action is idempotent, because a run record
/// narrows the repeat window but does not close it: the process may fall between an action and the
/// write of its position, and the delivery comes again.
/// </summary>
/// <remarks>
/// Idempotency is a property of the target, not of a check here: retention is keyed by the episode
/// key VmsFs owns, an emitted event carries an id derived from (subscription, trigger, position),
/// a relay is set to a value rather than toggled, and an alarm is deduplicated by
/// (object, dedup_key) among its open rows.
/// </remarks>
public sealed class ActionExecutor(IMapGateway map, INodeActions nodes)
{
  public async Task ExecuteAsync(
    SubscriptionSnapshot subscription,
    EventFacts trigger,
    CausationChain chain,
    ActionSpec action,
    CancellationToken ct)
  {
    switch (action.Kind)
    {
      case Routing.ActionRetainRecording:
        await RetainAsync(subscription, trigger, action, ct);
        break;

      case Routing.ActionSetRelay:
        await RelayAsync(action, ct);
        break;

      case Routing.ActionEmitEvent:
        await EmitAsync(subscription, trigger, chain, action, ct);
        break;

      case Routing.ActionRaiseAlarm:
        await RaiseAsync(subscription, trigger, action, ct);
        break;

      default:
        throw new InvalidOperationException($"unknown action kind '{action.Kind}'");
    }
  }

  private async Task RetainAsync(
    SubscriptionSnapshot subscription, EventFacts trigger, ActionSpec action, CancellationToken ct)
  {
    var parameters = action.Params;
    var timeout = parameters.GetProperty("timeout_seconds").GetInt32();
    var raiseAlarm = parameters.TryGetProperty("raise_alarm", out var raise) && raise.GetBoolean();
    var template = parameters.TryGetProperty("event_id", out var idTemplate)
      ? idTemplate.GetString()
      : null;

    var cameras = parameters.GetProperty("cameras").EnumerateArray()
      .Select(x => x.GetString()!)
      .ToList();

    // Every camera of one action shares the episode key except for the {camera_id} part of the
    // template: VmsFs opens one episode per (event_id, camera_id), so a rule that retains two
    // cameras leaves two episodes — which is what the operator asked for, and what makes a repeat
    // extend both rather than open two more.
    foreach (var camera in cameras)
    {
      var cameraId = camera == Routing.SourceCamera ? RequireSourceCamera(trigger) : camera;
      var eventId = Routing.ExpandTemplate(
        template ?? Routing.DefaultRetentionEventId, subscription.Id, trigger.ObjectId, cameraId);

      await nodes.RetainRecordingAsync(
        cameraId,
        new RetainRecordingCommand(eventId, timeout, raiseAlarm, DateTime.UtcNow),
        ct);
    }
  }

  private Task RelayAsync(ActionSpec action, CancellationToken ct)
  {
    var parameters = action.Params;
    return nodes.SetRelayAsync(
      parameters.GetProperty("camera_id").GetString()!,
      parameters.GetProperty("token").GetString()!,
      parameters.GetProperty("active").GetBoolean(),
      ct);
  }

  private Task EmitAsync(
    SubscriptionSnapshot subscription,
    EventFacts trigger,
    CausationChain chain,
    ActionSpec action,
    CancellationToken ct)
  {
    var parameters = action.Params;
    var after = chain.After(subscription.Id);

    var props = new Dictionary<string, string>(StringComparer.Ordinal);
    if (parameters.TryGetProperty("props", out var configured))
    {
      foreach (var property in configured.EnumerateObject())
        props[property.Name] = Scalar(property.Value);
    }

    // The chain is written last: an operator's props may not forge the path that stops a loop.
    props[Routing.PropCausationId] = trigger.Id;
    props[Routing.PropOrigin] = Routing.OriginSubscription;
    props[Routing.PropDepth] = after.Depth.ToString(CultureInfo.InvariantCulture);
    props[Routing.PropCausationChain] = after.Serialize();
    props[Routing.PropSubscriptionId] = subscription.Id;

    string Parameter(string name) =>
      parameters.TryGetProperty(name, out var value) ? value.GetString() ?? string.Empty : string.Empty;

    return map.EmitEventAsync(new EmitEventCommand(
      Id: DeterministicEventId(subscription.Id, trigger.Id, action.Position),
      ObjectId: Target(parameters, trigger),
      EventName: Parameter("event_name"),
      Param0: Parameter("param0"),
      Param1: Parameter("param1"),
      Props: props), ct);
  }

  private Task RaiseAsync(
    SubscriptionSnapshot subscription, EventFacts trigger, ActionSpec action, CancellationToken ct)
  {
    var parameters = action.Params;
    var dedup = parameters.TryGetProperty("dedup_key", out var template)
      ? template.GetString()
      : null;

    return map.RaiseAlarmAsync(new RaiseAlarmCommand(
      ObjectId: Guid.Parse(Target(parameters, trigger)),
      Source: parameters.GetProperty("definition").GetString()!,
      DedupKey: Routing.ExpandTemplate(
        string.IsNullOrEmpty(dedup) ? Routing.DefaultRetentionEventId : dedup,
        subscription.Id, trigger.ObjectId, trigger.Prop("camera_id")),
      SubscriptionId: Guid.Parse(subscription.Id),
      SourceEventId: Guid.Parse(trigger.Id)), ct);
  }

  private static string Target(JsonElement parameters, EventFacts trigger)
  {
    var target = parameters.GetProperty("object_id").GetString()!;
    return target == Routing.SourceObject ? trigger.ObjectId : target;
  }

  private static string RequireSourceCamera(EventFacts trigger) =>
    trigger.Prop("camera_id")
    ?? throw new InvalidOperationException(
      $"the event '{trigger.EventName}' carries no camera_id for '{Routing.SourceCamera}'");

  // A UUID derived from what the action is, not from the clock: a repeat computes the same id, and
  // VmsMap's insert is ON CONFLICT DO NOTHING. MD5 is a name-mangler here, not a digest.
  private static Guid DeterministicEventId(string subscriptionId, string triggerEventId, int position)
  {
    var bytes = Encoding.UTF8.GetBytes($"{subscriptionId}:{triggerEventId}:{position}");
    return new Guid(MD5.HashData(bytes));
  }

  private static string Scalar(JsonElement value) =>
    value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText();
}
