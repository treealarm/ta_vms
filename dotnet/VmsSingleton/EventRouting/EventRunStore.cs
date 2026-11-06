using System.Text.Json.Serialization;
using Dapr.Client;

namespace VmsSingleton.EventRouting;

/// <summary>
/// The run records and cooldowns, in the shared state store rather than in memory.
/// </summary>
/// <remarks>
/// At-least-once delivery means the same event can arrive twice: the handler crashed before
/// acknowledging, the acknowledgement was lost, the subscriber restarted. The record is what makes
/// the second delivery continue the run instead of starting it again, and it lives in the state
/// store so a restarted process finds it. The TTL is the backstop — a run nobody will continue
/// again does not need to be kept forever.
/// </remarks>
public sealed class DaprEventRunStore(DaprClient dapr, string stateStoreName) : IRunStore
{
  private const string CooldownPrefix = "event-cooldown:";

  private static readonly Dictionary<string, string> RunMetadata = new()
  {
    ["ttlInSeconds"] = ((int)Routing.RunTtl.TotalSeconds).ToString(),
  };

  public async Task<EventRun?> GetAsync(
    string subscriptionId, string triggerEventId, CancellationToken ct) =>
    await dapr.GetStateAsync<EventRun>(
      stateStoreName, EventRun.Key(subscriptionId, triggerEventId), cancellationToken: ct);

  public async Task SaveAsync(EventRun run, CancellationToken ct) =>
    await dapr.SaveStateAsync(
      stateStoreName,
      EventRun.Key(run.SubscriptionId, run.TriggerEventId),
      run,
      metadata: RunMetadata,
      cancellationToken: ct);

  public async Task<bool> IsCoolingDownAsync(
    string subscriptionId, string sourceObjectId, TimeSpan cooldown, CancellationToken ct)
  {
    if (cooldown <= TimeSpan.Zero)
      return false;

    var last = await dapr.GetStateAsync<CooldownStamp>(
      stateStoreName, CooldownKey(subscriptionId, sourceObjectId), cancellationToken: ct);

    return last is not null && DateTime.UtcNow - last.StartedAt < cooldown;
  }

  public async Task StartCooldownAsync(
    string subscriptionId, string sourceObjectId, TimeSpan cooldown, CancellationToken ct)
  {
    if (cooldown <= TimeSpan.Zero)
      return;

    await dapr.SaveStateAsync(
      stateStoreName,
      CooldownKey(subscriptionId, sourceObjectId),
      new CooldownStamp { StartedAt = DateTime.UtcNow },
      metadata: new Dictionary<string, string>
      {
        // Self-cleaning: the key outlives its own window by nothing, and a stale entry cannot
        // outlive a restart.
        ["ttlInSeconds"] = ((int)Math.Max(1, cooldown.TotalSeconds)).ToString(),
      },
      cancellationToken: ct);
  }

  private static string CooldownKey(string subscriptionId, string sourceObjectId) =>
    $"{CooldownPrefix}{subscriptionId}:{sourceObjectId}";

  private sealed class CooldownStamp
  {
    [JsonPropertyName("started_at")]
    public DateTime StartedAt { get; set; }
  }
}
