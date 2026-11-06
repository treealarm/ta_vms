using System.Globalization;

namespace VmsSingleton.EventRouting;

/// <summary>
/// How many subscription hops separate an event from the one that started the chain, and which
/// subscriptions are already on it.
/// </summary>
/// <remarks>
/// This is a guard on the model, not a general cycle detector. Two rules stop the demo's
/// "event → subscription → event" from running forever: the same subscription may not fire twice
/// on one chain, and a chain may not be deeper than <see cref="Routing.MaxDepth"/>. Both are
/// deliberately blunt — a legal A → B → A of two subscriptions that each toggle the other's relay
/// is still a loop, and the depth cap is what ends it.
/// </remarks>
public sealed record CausationChain(int Depth, IReadOnlyList<string> SubscriptionIds)
{
  public static CausationChain FromEvent(EventFacts evt)
  {
    var depth = int.TryParse(evt.Prop(Routing.PropDepth), NumberStyles.Integer,
      CultureInfo.InvariantCulture, out var parsed) && parsed > 0
      ? parsed
      : 0;

    var chain = evt.Prop(Routing.PropCausationChain);
    var ids = string.IsNullOrWhiteSpace(chain)
      ? []
      : chain.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    return new CausationChain(depth, ids);
  }

  /// <summary>False when firing this subscription would close a loop or run too deep.</summary>
  public bool CanFire(string subscriptionId) =>
    Depth <= Routing.MaxDepth && !SubscriptionIds.Contains(subscriptionId, StringComparer.Ordinal);

  /// <summary>The chain an event emitted by <paramref name="subscriptionId"/> carries.</summary>
  public CausationChain After(string subscriptionId) =>
    new(Depth + 1, [.. SubscriptionIds, subscriptionId]);

  /// <summary>The prop value an emitted event carries: the whole path, comma-separated.</summary>
  public string Serialize() => string.Join(',', SubscriptionIds);
}
