using System.Globalization;

namespace VmsSingleton.EventRouting;

/// <summary>
/// Whether an event is an occurrence of what a subscription selects.
/// </summary>
/// <remarks>
/// The selector is deliberately plain: every list is an OR within itself, and the fields are ANDed
/// together. An empty or absent list means "any"; a non-empty one never matches an event that does
/// not carry the field, because "no camera_id" is a different thing from "any camera". `props` is
/// string equality except <c>min_confidence</c>, which is a floor.
/// </remarks>
public static class SubscriptionMatcher
{
  public const string ArmedConditionAny = "any";
  public const string ArmedConditionArmed = "armed";
  public const string ArmedConditionDisarmed = "disarmed";

  /// <summary>Event name and source selector, without the arming question.</summary>
  public static bool Selects(SubscriptionSnapshot subscription, EventFacts evt)
  {
    if (!string.Equals(subscription.EventName, evt.EventName, StringComparison.Ordinal))
      return false;

    var selector = subscription.Selector;

    if (selector.ObjectIds.Count > 0
        && !selector.ObjectIds.Contains(evt.ObjectId, StringComparer.Ordinal))
      return false;

    if (!ListMatches(selector.CameraIds, evt.Prop("camera_id")))
      return false;

    if (!ListMatches(selector.Kinds, evt.Prop("part_kind")))
      return false;

    if (!ListMatches(selector.Tokens, evt.Prop("token")))
      return false;

    foreach (var (name, expected) in selector.Props)
    {
      if (!string.Equals(evt.Prop(name), expected, StringComparison.Ordinal))
        return false;
    }

    if (selector.MinConfidence is { } minimum)
    {
      // The selector's floor reads the event's `confidence` prop (analytics writes it under that
      // name, see m2.4e); the selector key is `min_confidence` because it is a threshold, not an
      // equality.
      var actual = evt.Prop("confidence");
      if (actual is null
          || !double.TryParse(actual, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
          || value < minimum)
        return false;
    }

    return true;
  }

  /// <summary>
  /// The arming half. <paramref name="armed"/> is null when the answer is not available — the
  /// event's node is not a device, or VmsMap did not answer — and null satisfies neither "armed"
  /// nor "disarmed": a rule that asks about arming must not fire on an unknown answer.
  /// </summary>
  public static bool ArmedConditionMatches(SubscriptionSnapshot subscription, bool? armed) =>
    subscription.ArmedCondition switch
    {
      ArmedConditionArmed => armed == true,
      ArmedConditionDisarmed => armed == false,
      _ => true,
    };

  public static bool Matches(SubscriptionSnapshot subscription, EventFacts evt, bool? armed) =>
    Selects(subscription, evt) && ArmedConditionMatches(subscription, armed);

  private static bool ListMatches(IReadOnlyList<string> allowed, string? actual)
  {
    if (allowed.Count == 0)
      return true;

    return actual is not null && allowed.Contains(actual, StringComparer.Ordinal);
  }
}
