using Dapr.Messaging.PublishSubscribe;
using System.Text.Json;

namespace VmsFs;

public sealed class CameraStorageSubscriber(
  DaprPublishSubscribeClient pubSubClient,
  CameraStorageCache storageCache,
  StorageListCache storageListCache,
  ILogger<CameraStorageSubscriber> logger) : IHostedService, IDisposable
{
  private const string CameraStorageChangedTopic = "camera-storage-changed";
  // A storage itself changed — path, reservation, taken out of service, removed. Which cameras
  // that affects is not in the payload, so both caches are dropped whole.
  private const string StorageConfigChangedTopic = "storage-config-changed";
  private static readonly string PubSubName =
    Environment.GetEnvironmentVariable("PUBSUB_NAME")
      ?? throw new InvalidOperationException("Required environment variable 'PUBSUB_NAME' is not set");

  private readonly CancellationTokenSource _cts = new();

  public Task StartAsync(CancellationToken cancellationToken)
  {
    _ = RunAsync(_cts.Token);
    return Task.CompletedTask;
  }

  private async Task RunAsync(CancellationToken ct)
  {
    try
    {
      var options = new DaprSubscriptionOptions(
        new MessageHandlingPolicy(TimeSpan.FromSeconds(10), TopicResponseAction.Retry));

      await Task.WhenAll(
        pubSubClient.SubscribeAsync(PubSubName, CameraStorageChangedTopic, options, HandleAsync, ct),
        pubSubClient.SubscribeAsync(PubSubName, StorageConfigChangedTopic, options, HandleStorageAsync, ct));
    }
    catch (OperationCanceledException) { }
    catch (Exception ex)
    {
      logger.LogError(ex, "CameraStorageSubscriber error");
    }
  }

  private Task<TopicResponseAction> HandleAsync(TopicMessage message, CancellationToken _)
  {
    try
    {
      var cameraId = JsonSerializer.Deserialize<string>(message.Data.Span);
      if (!string.IsNullOrWhiteSpace(cameraId))
      {
        storageCache.Invalidate(cameraId);
        logger.LogDebug("Camera '{CameraId}' storage cache invalidated", cameraId);
      }
      return Task.FromResult(TopicResponseAction.Success);
    }
    catch
    {
      return Task.FromResult(TopicResponseAction.Retry);
    }
  }

  private Task<TopicResponseAction> HandleStorageAsync(TopicMessage message, CancellationToken _)
  {
    try
    {
      var storageId = JsonSerializer.Deserialize<string>(message.Data.Span);
      // The list first: the per-camera cache is built from it, so clearing that one first would
      // let a refresh in between rebuild it from the stale list.
      storageListCache.Invalidate();
      storageCache.InvalidateAll();
      logger.LogInformation(
        "Storage '{StorageId}' changed — storage caches dropped", storageId);
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
