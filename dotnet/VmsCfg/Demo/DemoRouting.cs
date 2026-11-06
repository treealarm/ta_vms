using System.Text.Json;
using System.Text.Json.Nodes;
using Vms.Cfg;

namespace VmsCfg.Demo;

/// The demo's one reaction: the sensor contact starts a retention episode on both cameras and
/// lights the hall's lamp. It is a subscription like any an administrator writes — the demo goes
/// through the same validation and the same router — with a fixed id so installing twice updates
/// the same row instead of adding a second rule.
public sealed class DemoRouting(EventSubscriptionStorage subscriptions)
{
  public const string Name = "Demo sensor recording";

  // A constant of its own, as the wall's is: a second install has to name the same row.
  public static readonly Guid SubscriptionId = new("053df083-59b0-56b3-a28c-c5701d1fc3cf");

  public sealed record Plan(bool Installed, IReadOnlyList<string> Skipped);

  /// The rule, described once. `camera_ids` and the action cameras come from the ids the demo
  /// cameras are configured under — the same derivation VmsMap's DemoIds and OnvifSim use.
  public static EventSubscription Describe()
  {
    var master = DemoCameraIds.For("sim-master");
    var ptz = DemoCameraIds.For("sim-ptz");

    var subscription = new EventSubscription
    {
      Id = SubscriptionId.ToString(),
      Name = Name,
      Enabled = true,
      EventName = "sensor",
      // The input is addressed by camera and token: no other device's `input` may fire it, and the
      // contact is the value the arming below treats as the trigger.
      SourceFilter = JsonSerializer.Serialize(new
      {
        camera_ids = new[] { ptz },
        tokens = new[] { "input" },
        props = new Dictionary<string, string> { ["contact"] = DemoDevices.SensorTrigger },
      }),
      ArmedCondition = EventSubscriptionRules.Any,
      CooldownMs = 0,
    };

    subscription.Actions.Add(new SubscriptionAction
    {
      Position = 0,
      Kind = EventSubscriptionRules.RetainRecording,
      Params = JsonSerializer.Serialize(new
      {
        cameras = new[] { master, ptz },
        timeout_seconds = 60,
        // The contact already raises its own alarm through the sensor rule; this action keeps the
        // clip, and a second alarm here would double every episode.
        raise_alarm = false,
      }),
    });

    subscription.Actions.Add(new SubscriptionAction
    {
      Position = 1,
      Kind = EventSubscriptionRules.SetRelay,
      Params = JsonSerializer.Serialize(new
      {
        camera_id = master,
        token = "lamp",
        active = true,
      }),
    });

    return subscription;
  }

  public async Task<Plan> InstallAsync(IReadOnlyCollection<string> presentCameraIds, CancellationToken ct)
  {
    var master = DemoCameraIds.For("sim-master");
    var ptz = DemoCameraIds.For("sim-ptz");

    // Both cameras are named by the rule, and the storage refuses a subscription that names a
    // camera the deployment lacks — skip rather than fail the whole install.
    if (!presentCameraIds.Contains(master) || !presentCameraIds.Contains(ptz))
      return new Plan(false, ["sim-master", "sim-ptz"]);

    var desired = Describe();
    var existing = await subscriptions.GetAsync(SubscriptionId.ToString(), ct);

    // A reinstall of an unchanged world is a no-op, not a second version: readers would reload
    // their caches for nothing.
    if (existing is not null && Same(existing, desired))
      return new Plan(true, []);

    var (saved, error) = await subscriptions.UpsertAsync(desired, ct);
    if (saved is null)
      throw new InvalidOperationException(
        $"The demo subscription could not be saved: {error}");

    return new Plan(true, []);
  }

  /// Takes the rule away with the world. The cameras and their settings stay: they belong to the
  /// deployment, not to the demo.
  public async Task<bool> RemoveAsync(CancellationToken ct)
  {
    var (removed, _) = await subscriptions.RemoveAsync(SubscriptionId.ToString(), ct);
    return removed;
  }

  /// <summary>
  /// Whether the stored row already says what the demo wants. The JSON is compared as data, not as
  /// text: `source_filter` and `params` are jsonb columns, and PostgreSQL hands them back with its
  /// own key order and spacing — a string comparison would rewrite the row on every install.
  /// </summary>
  private static bool Same(EventSubscription stored, EventSubscription desired) =>
    stored.Name == desired.Name
    && stored.Enabled == desired.Enabled
    && stored.EventName == desired.EventName
    && SameJson(stored.SourceFilter, desired.SourceFilter)
    && stored.ArmedCondition == desired.ArmedCondition
    && stored.CooldownMs == desired.CooldownMs
    && stored.Actions.Count == desired.Actions.Count
    && stored.Actions.Zip(desired.Actions).All(pair =>
      pair.First.Position == pair.Second.Position
      && pair.First.Kind == pair.Second.Kind
      && SameJson(pair.First.Params, pair.Second.Params));

  private static bool SameJson(string? left, string? right) =>
    JsonNode.DeepEquals(
      JsonNode.Parse(string.IsNullOrWhiteSpace(left) ? "{}" : left),
      JsonNode.Parse(string.IsNullOrWhiteSpace(right) ? "{}" : right));
}
