using Dapr.Messaging.PublishSubscribe;

namespace VmsAnalyticsSingleton;

// On startup and whenever analytics-watch-changed or camera-state-changed fires, refreshes
// AnalyticsWatchCache wholesale and re-runs WatchReconciler — mirrors the full-reconciliation
// style of media_server/config_subscriber.h rather than patching a single entity.
public sealed class AnalyticsWatchSubscriber(
  DaprPublishSubscribeClient pubSubClient,
  AnalyticsWatchCache cache,
  WatchReconciler reconciler,
  CameraStorageResolver storageResolver,
  ILogger<AnalyticsWatchSubscriber> logger) : IHostedService, IDisposable
{
  private const string AnalyticsWatchChangedTopic = "analytics-watch-changed";
  private const string CameraStateChangedTopic    = "camera-state-changed";
  private const string StorageConfigChangedTopic  = "storage-config-changed";
  private static readonly string PubSubName =
    Environment.GetEnvironmentVariable("PUBSUB_NAME")
      ?? throw new InvalidOperationException("Required environment variable 'PUBSUB_NAME' is not set");

  private readonly CancellationTokenSource _cts = new();

  public Task StartAsync(CancellationToken cancellationToken)
  {
    _ = RunAsync(_cts.Token);
    return Task.CompletedTask;
  }

  // Safety net for the case where the very first refresh at startup fails (e.g. VmsCfg's own
  // Dapr sidecar isn't up yet) and no analytics-watch-changed/camera-state-changed event happens
  // to arrive afterward — without this, the cache would stay empty forever.
  private static readonly TimeSpan FallbackResyncInterval = TimeSpan.FromMinutes(2);

  private async Task RunAsync(CancellationToken ct)
  {
    try
    {
      // Startup: VmsCfg (or its sidecar) may not be up yet — retry quickly until the first
      // successful refresh instead of leaving the initial reconcile to the slow fallback tick
      // (same "retry in 5s" startup pattern as DbMigrator / ServerPairRegistry init).
      while (!ct.IsCancellationRequested && !await RefreshAndReconcileAsync(ct))
        await Task.Delay(TimeSpan.FromSeconds(5), ct);

      var options = new DaprSubscriptionOptions(
        new MessageHandlingPolicy(TimeSpan.FromSeconds(10), TopicResponseAction.Retry));
      await Task.WhenAll(
        pubSubClient.SubscribeAsync(PubSubName, AnalyticsWatchChangedTopic, options, HandleAsync, ct),
        pubSubClient.SubscribeAsync(PubSubName, CameraStateChangedTopic, options, HandleAsync, ct),
        pubSubClient.SubscribeAsync(PubSubName, StorageConfigChangedTopic, options, HandleStorageChangedAsync, ct),
        FallbackResyncLoopAsync(ct));
    }
    catch (OperationCanceledException) { }
    catch (Exception ex)
    {
      logger.LogError(ex, "AnalyticsWatchSubscriber error");
    }
  }

  private async Task FallbackResyncLoopAsync(CancellationToken ct)
  {
    while (!ct.IsCancellationRequested)
    {
      await Task.Delay(FallbackResyncInterval, ct);
      await RefreshAndReconcileAsync(ct);
    }
  }

  private Task<TopicResponseAction> HandleAsync(TopicMessage message, CancellationToken ct)
  {
    _ = RefreshAndReconcileAsync(ct);
    return Task.FromResult(TopicResponseAction.Success);
  }

  private Task<TopicResponseAction> HandleStorageChangedAsync(TopicMessage message, CancellationToken ct)
  {
    // Event is only a signal to re-pull (CLAUDE.md pub/sub rule) — drop the caches, the next
    // crop save / janitor pass re-fetches storage assignments from VmsCfg.
    storageResolver.InvalidateAll();
    return Task.FromResult(TopicResponseAction.Success);
  }

  private async Task<bool> RefreshAndReconcileAsync(CancellationToken ct)
  {
    try
    {
      var refreshed = await cache.RefreshAsync(ct);
      await reconciler.ReconcileAsync(cache.Watches, ct);
      return refreshed;
    }
    catch (Exception ex)
    {
      logger.LogWarning(ex, "AnalyticsWatchSubscriber refresh/reconcile failed");
      return false;
    }
  }

  public Task StopAsync(CancellationToken _)
  {
    _cts.Cancel();
    return Task.CompletedTask;
  }

  public void Dispose() => _cts.Dispose();
}
