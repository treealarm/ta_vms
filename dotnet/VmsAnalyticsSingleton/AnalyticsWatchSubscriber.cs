using System.Text.Json;
using Dapr.Messaging.PublishSubscribe;
using VmsLog;

namespace VmsAnalyticsSingleton;

// On startup and whenever analytics-watch-changed or camera-state-changed fires, refreshes
// AnalyticsWatchCache wholesale and re-runs WatchReconciler — mirrors the full-reconciliation
// style of video_srv/config_subscriber.h rather than patching a single entity.
public sealed class AnalyticsWatchSubscriber(
  DaprPublishSubscribeClient pubSubClient,
  AnalyticsWatchCache cache,
  WatchReconciler reconciler,
  CameraStorageResolver storageResolver,
  CameraServerCache serverCache,
  ILogger<AnalyticsWatchSubscriber> logger) : IHostedService, IDisposable
{
  private const string AnalyticsWatchChangedTopic = "analytics-watch-changed";
  private const string CameraStateChangedTopic    = "camera-state-changed";
  private const string StorageConfigChangedTopic  = "storage-config-changed";
  private const string ServerConfigChangedTopic   = "server-config-changed";
  private static readonly string PubSubName =
    Environment.GetEnvironmentVariable("PUBSUB_NAME")
      ?? throw new InvalidOperationException("Required environment variable 'PUBSUB_NAME' is not set");

  private readonly CancellationTokenSource _cts = new();

  public Task StartAsync(CancellationToken cancellationToken)
  {
    _ = RunAsync(_cts.Token);
    return Task.CompletedTask;
  }

  // Safety net for events that never arrive: a first refresh at startup that failed (VmsCfg or its
  // sidecar not up yet) with no event after it, or a stream the sidecar closed cleanly, which
  // Dapr.Messaging 1.18.7 does not report (a failed one is opened again by StreamingSubscriptions).
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
        StreamingSubscriptions.KeepAsync(pubSubClient, PubSubName, options,
          [
            new(AnalyticsWatchChangedTopic, HandleAsync),
            new(CameraStateChangedTopic, HandleAsync),
            new(StorageConfigChangedTopic, HandleStorageChangedAsync),
            new(ServerConfigChangedTopic, HandleServerChangedAsync),
          ], logger, ct),
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

  // The refresh runs past the handler, so on the service's own token: the one passed in is the
  // message's, and the receiver disposes of it as soon as this returns.
  private Task<TopicResponseAction> HandleAsync(TopicMessage message, CancellationToken messageToken)
  {
    _ = RefreshAndReconcileAsync(_cts.Token);
    return Task.FromResult(TopicResponseAction.Success);
  }

  // Wire shape of server-config-changed; VmsCfg owns it. Empty CameraIds means "something node-wide
  // moved". Not filtered by server_id: this service is the one instance for the whole deployment,
  // so every node's cameras are its business.
  private sealed record ServerConfigChanged(string? ServerId, IReadOnlyList<string>? CameraIds);

  private static readonly JsonSerializerOptions JsonOptions =
    new() { PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

  private Task<TopicResponseAction> HandleServerChangedAsync(TopicMessage message, CancellationToken _)
  {
    // A camera moving to another node changes which VmsFs its detections and metadata belong to.
    // The event is only a signal to re-pull (CLAUDE.md pub/sub rule) — drop the entries and let the
    // next detection resolve the current owner.
    try
    {
      var evt = JsonSerializer.Deserialize<ServerConfigChanged>(message.Data.Span, JsonOptions);
      if (evt is null)
        return Task.FromResult(TopicResponseAction.Drop);

      serverCache.Invalidate(evt.CameraIds ?? []);
      return Task.FromResult(TopicResponseAction.Success);
    }
    catch
    {
      return Task.FromResult(TopicResponseAction.Retry);
    }
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
