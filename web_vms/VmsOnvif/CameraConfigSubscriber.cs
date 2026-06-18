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
  private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);
  private static readonly string PubSubName =
    Environment.GetEnvironmentVariable("PUBSUB_NAME")
      ?? throw new InvalidOperationException("Required environment variable 'PUBSUB_NAME' is not set");

  private readonly CancellationTokenSource _cts = new();

  public Task StartAsync(CancellationToken cancellationToken)
  {
    _ = RunAsync(_cts.Token);
    return Task.CompletedTask;
  }

  // Subscriptions run for the lifetime of the process — if the underlying stream
  // breaks (sidecar restart, transient redis hiccup, etc.) we must resubscribe,
  // otherwise CameraConnCache/OnvifFilterCache/CameraArmedCache silently stop
  // invalidating and start serving stale data forever.
  private async Task RunAsync(CancellationToken ct)
  {
    while (!ct.IsCancellationRequested)
    {
      try
      {
        var configTask = pubSubClient.SubscribeAsync(
          PubSubName,
          CameraConfigChangedTopic,
          new DaprSubscriptionOptions(
            new MessageHandlingPolicy(TimeSpan.FromSeconds(10), TopicResponseAction.Retry)),
          HandleConfigChangedAsync,
          ct);
        // Starting both streaming SubscribeAsync calls in the same instant makes the dapr
        // sidecar's redis-streams pubsub component spiral into a tight, unthrottled
        // "connection pool timeout" retry loop (confirmed: pegs the process at 800%+ CPU
        // and never finishes binding Kestrel). Staggering the second call avoids the race.
        await Task.Delay(200, ct);
        var stateTask = pubSubClient.SubscribeAsync(
          PubSubName,
          CameraStateChangedTopic,
          new DaprSubscriptionOptions(
            new MessageHandlingPolicy(TimeSpan.FromSeconds(10), TopicResponseAction.Retry)),
          HandleStateChangedAsync,
          ct);
        await Task.WhenAll(configTask, stateTask);
      }
      catch (OperationCanceledException)
      {
        break;
      }
      catch (Exception ex)
      {
        logger.LogError(ex,
          "CameraConfigSubscriber subscription failed, resubscribing in {DelaySeconds}s", RetryDelay.TotalSeconds);
        try
        {
          await Task.Delay(RetryDelay, ct);
        }
        catch (OperationCanceledException)
        {
          break;
        }
      }
    }
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
