using Dapr.Messaging.PublishSubscribe;
using System.Text.Json;

namespace VmsOnvif;

public sealed class CameraConfigSubscriber(
  DaprPublishSubscribeClient pubSubClient,
  OnvifFilterCache filterCache,
  CameraArmedCache armedCache,
  CameraConnCache connCache,
  OnvifEventSubscriptionService subscriptionService,
  ILogger<CameraConfigSubscriber> logger) : IHostedService, IDisposable
{
  private const string CameraConfigChangedTopic = "camera-config-changed";
  private const string CameraStateChangedTopic  = "camera-state-changed";
  private static readonly string PubSubName =
    Environment.GetEnvironmentVariable("PUBSUB_NAME")
      ?? throw new InvalidOperationException("Required environment variable 'PUBSUB_NAME' is not set");

  // Safety net for missed/undelivered invalidation events (including the case where the
  // streaming subscription dies silently): periodically drop all caches so staleness is
  // bounded by this interval instead of lasting forever.
  private static readonly TimeSpan FallbackInvalidateInterval = TimeSpan.FromMinutes(1);

  private readonly CancellationTokenSource _cts = new();

  public Task StartAsync(CancellationToken cancellationToken)
  {
    _ = RunAsync(_cts.Token);
    _ = FallbackInvalidateLoopAsync(_cts.Token);
    return Task.CompletedTask;
  }

  // Subscribe ONCE, never in a loop (mirrors VmsAnalytics.AnalyticsWatchSubscriber). The
  // streaming SubscribeAsync task can complete silently (no exception) when the sidecar ends
  // the stream — a resubscribe loop around that spins every few seconds, and each iteration
  // registers a fresh redis-streams consumer in the sidecar without the old one being torn
  // down. Observed live: sidecar ConnectionID climbing past 60, its redis reclaim loops
  // exhausting the connection pool ("redis: connection pool timeout" ~10/sec), and unbounded
  // memory growth in BOTH the sidecar and this app. Staleness after a silently-dead stream is
  // instead bounded by FallbackInvalidateLoopAsync.
  private async Task RunAsync(CancellationToken ct)
  {
    try
    {
      var options = new DaprSubscriptionOptions(
        new MessageHandlingPolicy(TimeSpan.FromSeconds(10), TopicResponseAction.Retry));
      // Starting both streaming SubscribeAsync calls in the same instant makes the dapr
      // sidecar's redis-streams pubsub component spiral into a tight, unthrottled
      // "connection pool timeout" retry loop (confirmed: pegs the process at 800%+ CPU
      // and never finishes binding Kestrel). Staggering the second call avoids the race.
      var configTask = pubSubClient.SubscribeAsync(
        PubSubName, CameraConfigChangedTopic, options, HandleConfigChangedAsync, ct);
      await Task.Delay(200, ct);
      var stateTask = pubSubClient.SubscribeAsync(
        PubSubName, CameraStateChangedTopic, options, HandleStateChangedAsync, ct);

      await Task.WhenAll(configTask, stateTask);
      logger.LogWarning(
        "CameraConfigSubscriber streaming subscriptions ended — relying on the periodic fallback invalidation until restart");
    }
    catch (OperationCanceledException) { }
    catch (Exception ex)
    {
      logger.LogError(ex,
        "CameraConfigSubscriber subscription failed — relying on the periodic fallback invalidation until restart");
    }
  }

  private async Task FallbackInvalidateLoopAsync(CancellationToken ct)
  {
    try
    {
      while (!ct.IsCancellationRequested)
      {
        await Task.Delay(FallbackInvalidateInterval, ct);
        connCache.InvalidateAll();
        filterCache.InvalidateAll();
        armedCache.InvalidateAll();
      }
    }
    catch (OperationCanceledException) { }
  }

  private Task<TopicResponseAction> HandleConfigChangedAsync(TopicMessage message, CancellationToken _)
  {
    try
    {
      var cameraId = JsonSerializer.Deserialize<string>(message.Data.Span);
      if (!string.IsNullOrWhiteSpace(cameraId))
      {
        connCache.Invalidate(cameraId);
        filterCache.Invalidate(cameraId);
        subscriptionService.NotifyConfigChanged(cameraId);
      }
      return Task.FromResult(TopicResponseAction.Success);
    }
    catch
    {
      return Task.FromResult(TopicResponseAction.Retry);
    }
  }

  private Task<TopicResponseAction> HandleStateChangedAsync(TopicMessage message, CancellationToken _)
  {
    try
    {
      var cameraId = JsonSerializer.Deserialize<string>(message.Data.Span);
      if (!string.IsNullOrWhiteSpace(cameraId))
        armedCache.Invalidate(cameraId);
      return Task.FromResult(TopicResponseAction.Success);
    }
    catch
    {
      return Task.FromResult(TopicResponseAction.Retry);
    }
  }

  public Task StopAsync(CancellationToken _)
  {
    _cts.Cancel();
    return Task.CompletedTask;
  }

  public void Dispose() => _cts.Dispose();
}
