using System.Text.Json;
using System.Text.RegularExpressions;
using Vms.Cfg;

namespace VmsCfg;

/// <summary>
/// What a subscription may say, checked when it is saved rather than when it fires.
/// </summary>
/// <remarks>
/// The selector and every action's parameters are JSON, and the format is only as good as the
/// reader: an unknown key would be silently ignored by the router and the operator would spend an
/// afternoon on a rule that never matched. So a key the router does not read is an error here.
/// <para>
/// Camera existence is checked by the caller, against the table that owns cameras; this class only
/// names the ids to check. Object ids are VmsMap's tree, and VmsCfg does not read that database —
/// the map answers at run time, and a raise or an emit for a node that is gone fails where the node
/// lives rather than blocking a subscription that a map not yet built has no answer for.
/// </para>
/// </remarks>
public static class EventSubscriptionRules
{
  public const string Any = "any";
  public const string Armed = "armed";
  public const string Disarmed = "disarmed";

  /// <summary>"This event's own node" in an action parameter.</summary>
  public const string SourceObject = "source";

  /// <summary>"The camera whose part produced the event" in a retention action.</summary>
  public const string SourceCamera = "source_camera";

  public const string RetainRecording = "retain_recording";
  public const string SetRelay = "set_relay";
  public const string EmitEvent = "emit_event";
  public const string RaiseAlarm = "raise_alarm";

  public const string DefinitionSensor = "sensor";
  public const string DefinitionSubscription = "subscription";

  // The journal's dictionary (docs/plans/square-merge/m2.4-devices-alarms-automation.md §5):
  // what VmsMap writes as `event_name`, and therefore what a subscription may select.
  private static readonly HashSet<string> EventNames = new(StringComparer.Ordinal)
  {
    "sensor", "relay", "analytics",
    "alarm_raised", "alarm_acknowledged", "alarm_returned", "alarm_closed",
    // The retention episode's legacy name: it is still what VmsFs-driven alarms write.
    "alarm",
  };

  private static readonly HashSet<string> ArmedConditions = new(StringComparer.Ordinal)
  {
    Any, Armed, Disarmed,
  };

  private static readonly HashSet<string> PartKinds = new(StringComparer.Ordinal)
  {
    "digital_input", "relay",
  };

  private static readonly HashSet<string> SelectorKeys = new(StringComparer.Ordinal)
  {
    "object_ids", "camera_ids", "kinds", "tokens", "props",
  };

  private static readonly Dictionary<string, HashSet<string>> ActionKeys = new(StringComparer.Ordinal)
  {
    [RetainRecording] = ["cameras", "timeout_seconds", "event_id", "raise_alarm"],
    [SetRelay] = ["camera_id", "token", "active"],
    [EmitEvent] = ["object_id", "event_name", "param0", "param1", "props"],
    [RaiseAlarm] = ["object_id", "definition", "dedup_key"],
  };

  private static readonly HashSet<string> TemplatePlaceholders = new(StringComparer.Ordinal)
  {
    "subscription_id", "source_object_id", "camera_id",
  };

  private static readonly Regex Placeholder = new(@"\{([a-z_]+)\}", RegexOptions.Compiled);

  /// <summary>What to check before saving: the first thing wrong, and the cameras to look up.</summary>
  public sealed record CheckResult(string? Error, IReadOnlyList<string> CameraIds);

  public static CheckResult Check(EventSubscription subscription)
  {
    if (string.IsNullOrWhiteSpace(subscription.Name))
      return Fail("name must not be empty");

    if (!EventNames.Contains(subscription.EventName))
      return Fail($"'{subscription.EventName}' is not an event of the map's journal");

    if (!ArmedConditions.Contains(subscription.ArmedCondition))
      return Fail($"armed_condition must be one of {string.Join(", ", ArmedConditions)}");

    if (subscription.CooldownMs < 0)
      return Fail("cooldown_ms must not be negative");

    var cameras = new SortedSet<string>(StringComparer.Ordinal);

    var selector = ParseObject(subscription.SourceFilter, "source_filter");
    if (selector.Error is not null)
      return Fail(selector.Error);

    foreach (var property in selector.Value!.Value.EnumerateObject())
    {
      var key = property.Name;
      var value = property.Value;
      if (!SelectorKeys.Contains(key))
        return Fail($"unknown source_filter key '{key}'");

      switch (key)
      {
        case "object_ids":
        case "camera_ids":
          foreach (var id in Strings(value, $"source_filter.{key}"))
          {
            if (id.Error is not null)
              return Fail(id.Error);
            if (!Guid.TryParse(id.Value, out _))
              return Fail($"source_filter.{key} must contain UUIDs, got '{id.Value}'");
            if (key == "camera_ids")
              cameras.Add(id.Value);
          }
          break;

        case "kinds":
          foreach (var kind in Strings(value, "source_filter.kinds"))
          {
            if (kind.Error is not null)
              return Fail(kind.Error);
            if (!PartKinds.Contains(kind.Value!))
              return Fail($"source_filter.kinds must be digital_input or relay, got '{kind.Value}'");
          }
          break;

        case "tokens":
          foreach (var token in Strings(value, "source_filter.tokens"))
          {
            if (token.Error is not null)
              return Fail(token.Error);
          }
          break;

        case "props":
          var props = ParseProps(value, "source_filter.props");
          if (props is not null)
            return Fail(props);
          break;
      }
    }

    if (subscription.Actions.Count == 0)
      return Fail("a subscription needs at least one action");

    for (var i = 0; i < subscription.Actions.Count; i++)
    {
      var action = subscription.Actions[i];
      if (action.Position != i)
        return Fail($"action positions must be 0..{subscription.Actions.Count - 1} in order, "
          + $"got {action.Position} at index {i}");

      if (!ActionKeys.TryGetValue(action.Kind, out var allowed))
        return Fail($"unknown action kind '{action.Kind}'");

      var error = CheckAction(action, allowed, cameras);
      if (error is not null)
        return Fail(error);
    }

    return new CheckResult(null, cameras.ToList());
  }

  /// <summary>Expands an action's template with the run's own identifiers.</summary>
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

  private static string? CheckAction(
    SubscriptionAction action, HashSet<string> allowed, SortedSet<string> cameras)
  {
    var parameters = ParseObject(action.Params, $"{action.Kind}.params");
    if (parameters.Error is not null)
      return parameters.Error;

    var values = parameters.Value!.Value;

    foreach (var property in values.EnumerateObject())
    {
      if (!allowed.Contains(property.Name))
        return $"unknown {action.Kind} parameter '{property.Name}'";
    }

    switch (action.Kind)
    {
      case RetainRecording:
      {
        if (!values.TryGetProperty("cameras", out var list) || list.ValueKind != JsonValueKind.Array)
          return "retain_recording needs a cameras array";
        var entries = Strings(list, "retain_recording.cameras");
        if (entries.Count == 0)
          return "retain_recording needs at least one camera";
        foreach (var entry in entries)
        {
          if (entry.Error is not null)
            return entry.Error;
          if (entry.Value == SourceCamera)
            continue;
          if (!Guid.TryParse(entry.Value, out _))
            return $"retain_recording.cameras must contain UUIDs or '{SourceCamera}', got '{entry.Value}'";
          cameras.Add(entry.Value!);
        }

        if (!values.TryGetProperty("timeout_seconds", out var timeout)
            || timeout.ValueKind != JsonValueKind.Number
            || !timeout.TryGetInt32(out var seconds)
            || seconds is < 1 or > 3600)
          return "retain_recording.timeout_seconds must be an integer between 1 and 3600";

        if (values.TryGetProperty("event_id", out var eventId))
        {
          var error = CheckTemplate(eventId, "retain_recording.event_id");
          if (error is not null)
            return error;
        }
        if (values.TryGetProperty("raise_alarm", out var raise)
            && raise.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
          return "retain_recording.raise_alarm must be a boolean";
        break;
      }

      case SetRelay:
      {
        if (!values.TryGetProperty("camera_id", out var camera)
            || camera.ValueKind != JsonValueKind.String)
          return "set_relay needs a camera_id";
        var cameraId = camera.GetString()!;
        if (!Guid.TryParse(cameraId, out _))
          return $"set_relay.camera_id must be a UUID, got '{cameraId}'";
        cameras.Add(cameraId);

        if (!values.TryGetProperty("token", out var token)
            || token.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(token.GetString()))
          return "set_relay needs a token";
        if (!values.TryGetProperty("active", out var active)
            || active.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
          return "set_relay needs a boolean active";
        break;
      }

      case EmitEvent:
      {
        var error = CheckTarget(values, EmitEvent);
        if (error is not null)
          return error;
        if (!values.TryGetProperty("event_name", out var name)
            || name.ValueKind != JsonValueKind.String)
          return "emit_event needs an event_name";
        if (!EventNames.Contains(name.GetString()!))
          return $"'{name.GetString()}' is not an event of the map's journal";
        foreach (var text in new[] { "param0", "param1" })
        {
          if (values.TryGetProperty(text, out var value) && value.ValueKind != JsonValueKind.String)
            return $"emit_event.{text} must be a string";
        }
        if (values.TryGetProperty("props", out var props))
        {
          var propError = ParseProps(props, "emit_event.props");
          if (propError is not null)
            return propError;
        }
        break;
      }

      case RaiseAlarm:
      {
        var error = CheckTarget(values, RaiseAlarm);
        if (error is not null)
          return error;
        if (!values.TryGetProperty("definition", out var definition)
            || definition.ValueKind != JsonValueKind.String
            || definition.GetString() is not (DefinitionSensor or DefinitionSubscription))
          return $"raise_alarm.definition must be '{DefinitionSensor}' or '{DefinitionSubscription}'";
        if (values.TryGetProperty("dedup_key", out var dedup))
        {
          var templateError = CheckTemplate(dedup, "raise_alarm.dedup_key");
          if (templateError is not null)
            return templateError;
        }
        break;
      }
    }

    return null;
  }

  // `object_id` is the action's target node: a UUID, or the event's own node.
  private static string? CheckTarget(JsonElement values, string kind)
  {
    if (!values.TryGetProperty("object_id", out var target)
        || target.ValueKind != JsonValueKind.String)
      return $"{kind} needs an object_id";

    var text = target.GetString()!;
    if (text != SourceObject && !Guid.TryParse(text, out _))
      return $"{kind}.object_id must be a UUID or '{SourceObject}', got '{text}'";
    return null;
  }

  private static string? CheckTemplate(JsonElement value, string name)
  {
    if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
      return $"{name} must be a non-empty string";

    foreach (Match match in Placeholder.Matches(value.GetString()!))
    {
      if (!TemplatePlaceholders.Contains(match.Groups[1].Value))
        return $"{name} uses unknown placeholder '{match.Value}'";
    }

    return null;
  }

  private static string? ParseProps(JsonElement value, string name)
  {
    if (value.ValueKind != JsonValueKind.Object)
      return $"{name} must be an object";

    foreach (var property in value.EnumerateObject())
    {
      if (property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array
          or JsonValueKind.Null)
        return $"{name}.{property.Name} must be a string, number or boolean";

      if (property.Name == "min_confidence"
          && (property.Value.ValueKind != JsonValueKind.Number
              || !property.Value.TryGetDouble(out var confidence)
              || confidence is < 0 or > 1))
        return $"{name}.min_confidence must be a number between 0 and 1";
    }

    return null;
  }

  private static List<(string? Value, string? Error)> Strings(JsonElement value, string name)
  {
    var result = new List<(string?, string?)>();
    if (value.ValueKind != JsonValueKind.Array)
    {
      result.Add((null, $"{name} must be an array"));
      return result;
    }

    foreach (var item in value.EnumerateArray())
    {
      if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString()))
        result.Add((null, $"{name} must contain non-empty strings"));
      else
        result.Add((item.GetString(), null));
    }

    return result;
  }

  // The JSON columns are TEXT in this service's mapping: an empty or missing filter is the empty
  // object, which selects anything, and that is what a freshly created subscription starts as.
  private static (JsonElement? Value, string? Error) ParseObject(string? json, string name)
  {
    if (string.IsNullOrWhiteSpace(json))
      return (JsonDocument.Parse("{}").RootElement, null);

    try
    {
      using var document = JsonDocument.Parse(json);
      if (document.RootElement.ValueKind != JsonValueKind.Object)
        return (null, $"{name} must be a JSON object");

      // The document goes away with the using; the element the caller walks must own its bytes.
      return (document.RootElement.Clone(), null);
    }
    catch (JsonException ex)
    {
      return (null, $"{name} is not valid JSON: {ex.Message}");
    }
  }

  private static CheckResult Fail(string error) => new(error, []);
}
