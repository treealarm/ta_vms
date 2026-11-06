using System.Text.Json;
using Dapr.Messaging.PublishSubscribe;
using Dapr.Messaging.PublishSubscribe.Extensions;
using Vms.Cfg;
using VmsLog;

namespace VmsSingleton.EventRouting;

/// <summary>
/// VmsCfg's subscriptions, held as snapshots and reloaded when they change.
/// </summary>
/// <remarks>
/// VmsSingleton is the one instance the deployment guarantees, so the pub/sub signal alone is
/// enough here — unlike VmsMap, which a change reaches on one replica and reconciles through a
/// version key. The TTL is only the backstop for a lost message. Alarm definitions are not cached:
/// the router never resolves one — VmsMap turns the source name into a definition when it raises,
/// and a missing definition is its warning to log, not something the router could act on.
/// </remarks>
public sealed class SubscriptionCache : ISubscriptionSource, IHostedService, IDisposable
{
  private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(1);
  private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(5);
  private static readonly TimeSpan SubscriptionTimeout = TimeSpan.FromSeconds(10);

  private static readonly string PubSubName =
    Environment.GetEnvironmentVariable("PUBSUB_NAME")
    ?? throw new InvalidOperationException("Required environment variable 'PUBSUB_NAME' is not set");

  private readonly CfgService.CfgServiceClient _cfg;
  private readonly DaprPublishSubscribeClient _pubSub;
  private readonly ILogger<SubscriptionCache> _logger;
  private readonly SemaphoreSlim _gate = new(1, 1);
  private readonly CancellationTokenSource _cts = new();

  private IReadOnlyList<SubscriptionSnapshot>? _entries;
  private DateTime _loadedAtUtc = DateTime.MinValue;

  public SubscriptionCache(
    CfgService.CfgServiceClient cfg,
    DaprPublishSubscribeClient pubSub,
    ILogger<SubscriptionCache> logger)
  {
    _cfg = cfg ?? throw new ArgumentNullException(nameof(cfg));
    _pubSub = pubSub ?? throw new ArgumentNullException(nameof(pubSub));
    _logger = logger ?? throw new ArgumentNullException(nameof(logger));
  }

  public async Task<IReadOnlyList<SubscriptionSnapshot>> ListAsync(CancellationToken ct)
  {
    var cached = _entries;
    if (cached is not null && DateTime.UtcNow - _loadedAtUtc < CacheTtl)
      return cached;

    await _gate.WaitAsync(ct);
    try
    {
      if (_entries is not null && DateTime.UtcNow - _loadedAtUtc < CacheTtl)
        return _entries;

      var response = await _cfg.ListEventSubscriptionsAsync(
        new ListEventSubscriptionsRequest(),
        deadline: DateTime.UtcNow.Add(CallTimeout),
        cancellationToken: ct);

      _entries = response.Subscriptions
        .Where(x => x.Enabled)
        .Select(Parse)
        .Where(x => x is not null)
        .Select(x => x!)
        .ToList();
      _loadedAtUtc = DateTime.UtcNow;
      _logger.LogDebug("Loaded {Count} enabled event subscription(s)", _entries.Count);
      return _entries;
    }
    finally
    {
      _gate.Release();
    }
  }

  public void Invalidate() => _loadedAtUtc = DateTime.MinValue;

  public Task StartAsync(CancellationToken cancellationToken)
  {
    var options = new DaprSubscriptionOptions(
      new MessageHandlingPolicy(SubscriptionTimeout, TopicResponseAction.Retry));

    _ = StreamingSubscriptions.KeepAsync(_pubSub, PubSubName, options,
      [new(Routing.SubscriptionsChangedTopic, HandleAsync)], _logger, _cts.Token);

    return Task.CompletedTask;
  }

  // The payload is irrelevant: it says something moved, and the next event reloads the list.
  private Task<TopicResponseAction> HandleAsync(TopicMessage message, CancellationToken ct)
  {
    Invalidate();
    return Task.FromResult(TopicResponseAction.Success);
  }

  public Task StopAsync(CancellationToken cancellationToken)
  {
    _cts.Cancel();
    return Task.CompletedTask;
  }

  public void Dispose()
  {
    _cts.Dispose();
    _gate.Dispose();
  }

  // ── Parsing ─────────────────────────────────────────────────────────────────────────────────

  private SubscriptionSnapshot? Parse(EventSubscription subscription)
  {
    try
    {
      return new SubscriptionSnapshot(
        Id: subscription.Id,
        Name: subscription.Name,
        EventName: subscription.EventName,
        Selector: ParseSelector(subscription.SourceFilter),
        ArmedCondition: subscription.ArmedCondition,
        CooldownMs: subscription.CooldownMs,
        Version: subscription.Version,
        Actions: subscription.Actions
          .OrderBy(x => x.Position)
          .Select(x => new ActionSpec(x.Position, x.Kind, ParseJson(x.Params, "{}")))
          .ToList());
    }
    catch (JsonException ex)
    {
      // VmsCfg validated the row when it was saved, so this is a row written some other way. The
      // router's job is to keep serving the others, not to stop on one bad subscription.
      _logger.LogWarning(ex,
        "Skipping event subscription '{Id}' ({Name}): its JSON does not parse", subscription.Id, subscription.Name);
      return null;
    }
  }

  private static EventSelector ParseSelector(string? json)
  {
    using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
    var root = document.RootElement;

    var props = new Dictionary<string, string>(StringComparer.Ordinal);
    double? minConfidence = null;

    if (root.TryGetProperty("props", out var propObject))
    {
      foreach (var property in propObject.EnumerateObject())
      {
        if (property.Name == "min_confidence" && property.Value.TryGetDouble(out var minimum))
        {
          minConfidence = minimum;
          continue;
        }

        props[property.Name] = property.Value.ValueKind == JsonValueKind.String
          ? property.Value.GetString()!
          : property.Value.GetRawText();
      }
    }

    return new EventSelector(
      Strings(root, "object_ids"),
      Strings(root, "camera_ids"),
      Strings(root, "kinds"),
      Strings(root, "tokens"),
      props,
      minConfidence);
  }

  private static List<string> Strings(JsonElement root, string name) =>
    root.TryGetProperty(name, out var array) && array.ValueKind == JsonValueKind.Array
      ? array.EnumerateArray()
          .Where(x => x.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(x.GetString()))
          .Select(x => x.GetString()!)
          .ToList()
      : [];

  private static JsonElement ParseJson(string? json, string fallback)
  {
    using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? fallback : json);
    // The document is disposed with the using; the action keeps the bytes.
    return document.RootElement.Clone();
  }
}
