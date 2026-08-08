using System.Text.Json;
using Dapr.Messaging.PublishSubscribe;

namespace VmsUtils;

/// Keeps MediaMtxServerCache honest by listening to the config-change events VmsCfg already
/// publishes, rather than re-reading the assignment on every poll.
///
/// camera-config-changed carries the camera id, so only that entry is dropped.
/// media-server-config-changed carries a media server id and is what VmsCfg publishes when a
/// MediaMTX row itself is added, edited or removed (NotifyAllMediaServers) — it does not say which
/// cameras are affected and a changed host or api_port invalidates every entry pointing at that
/// server, so the whole cache goes. Both are rare, so dropping everything costs one re-resolve per
/// camera on the next poll.
public sealed class MediaMtxConfigSubscriber(
  DaprPublishSubscribeClient pubSubClient,
  MediaMtxServerCache serverCache,
  ILogger<MediaMtxConfigSubscriber> logger) : IHostedService, IDisposable
{
  private const string CameraConfigChangedTopic = "camera-config-changed";
  private const string MediaServerConfigChangedTopic = "media-server-config-changed";

  private static readonly string PubSubName =
    Environment.GetEnvironmentVariable("PUBSUB_NAME")
      ?? throw new InvalidOperationException("Required environment variable 'PUBSUB_NAME' is not set");

  private readonly CancellationTokenSource _cts = new();

  public Task StartAsync(CancellationToken cancellationToken)
  {
    _ = RunAsync(CameraConfigChangedTopic, HandleCameraChangedAsync, _cts.Token);
    _ = RunAsync(MediaServerConfigChangedTopic, HandleMediaServerChangedAsync, _cts.Token);
    return Task.CompletedTask;
  }

  private async Task RunAsync(string topic, TopicMessageHandler handler, CancellationToken ct)
  {
    try
    {
      await pubSubClient.SubscribeAsync(
        PubSubName,
        topic,
        new DaprSubscriptionOptions(
          new MessageHandlingPolicy(TimeSpan.FromSeconds(10), TopicResponseAction.Drop)),
        handler,
        ct);
    }
    catch (OperationCanceledException) { }
    catch (Exception ex)
    {
      logger.LogError(ex, "MediaMtxConfigSubscriber error on topic '{Topic}'", topic);
    }
  }

  private Task<TopicResponseAction> HandleCameraChangedAsync(TopicMessage message, CancellationToken _)
  {
    var cameraId = ReadStringPayload(message);
    if (!string.IsNullOrWhiteSpace(cameraId))
      serverCache.Invalidate(cameraId);
    return Task.FromResult(TopicResponseAction.Success);
  }

  private Task<TopicResponseAction> HandleMediaServerChangedAsync(TopicMessage message, CancellationToken _)
  {
    serverCache.Clear();
    return Task.FromResult(TopicResponseAction.Success);
  }

  // Both topics carry a bare JSON string, e.g. "vms".
  private string? ReadStringPayload(TopicMessage message)
  {
    try
    {
      return JsonSerializer.Deserialize<string>(message.Data.Span);
    }
    catch (JsonException ex)
    {
      logger.LogWarning(ex, "Dropping malformed config-change message");
      return null;
    }
  }

  public Task StopAsync(CancellationToken _)
  {
    _cts.Cancel();
    return Task.CompletedTask;
  }

  public void Dispose() => _cts.Dispose();
}
