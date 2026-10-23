using Dapr.Messaging.PublishSubscribe;
using System.Text.Json;
using VmsLog;

namespace VmsOnvif;

public sealed class CameraConfigSubscriber(
  DaprPublishSubscribeClient pubSubClient,
  OnvifFilterCache filterCache,
  CameraArmedCache armedCache,
  CameraConnCache connCache,
  OnvifMetadataGateCache metadataGate,
  OnvifEventSubscriptionService subscriptionService,
  string ownServerId,
  ILogger<CameraConfigSubscriber> logger) : IHostedService, IDisposable
{
  private const string ServerConfigChangedTopic = "server-config-changed";
  private const string CameraStateChangedTopic  = "camera-state-changed";
  private static readonly string PubSubName =
    Environment.GetEnvironmentVariable("PUBSUB_NAME")
      ?? throw new InvalidOperationException("Required environment variable 'PUBSUB_NAME' is not set");

  // Safety net for invalidation events that never arrive. A stream that fails is opened again
  // (StreamingSubscriptions), but one the sidecar closes cleanly goes unnoticed in Dapr.Messaging
  // 1.18.7, so caches are also dropped wholesale on this interval and staleness stays bounded by it.
  private static readonly TimeSpan FallbackInvalidateInterval = TimeSpan.FromMinutes(1);

  private readonly CancellationTokenSource _cts = new();

  public Task StartAsync(CancellationToken cancellationToken)
  {
    var options = new DaprSubscriptionOptions(
      new MessageHandlingPolicy(TimeSpan.FromSeconds(10), TopicResponseAction.Retry));
    _ = StreamingSubscriptions.KeepAsync(pubSubClient, PubSubName, options,
      [new(ServerConfigChangedTopic, HandleConfigChangedAsync), new(CameraStateChangedTopic, HandleStateChangedAsync)],
      logger, _cts.Token);
    _ = FallbackInvalidateLoopAsync(_cts.Token);
    return Task.CompletedTask;
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
        metadataGate.InvalidateAll();
      }
    }
    catch (OperationCanceledException) { }
  }

  // Wire shape of server-config-changed; VmsCfg owns it. Empty CameraIds means everything on the
  // node.
  private sealed record ServerConfigChanged(string? ServerId, IReadOnlyList<string>? CameraIds);

  private static readonly JsonSerializerOptions JsonOptions =
    new() { PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

  private Task<TopicResponseAction> HandleConfigChangedAsync(TopicMessage message, CancellationToken _)
  {
    try
    {
      var evt = JsonSerializer.Deserialize<ServerConfigChanged>(message.Data.Span, JsonOptions);
      if (evt is null)
        return Task.FromResult(TopicResponseAction.Drop);

      // Another node's change: dropped here, before any work. This is the whole point of the server
      // travelling with the event — deciding ownership used to mean fetching the camera from VmsCfg,
      // so a single edit cost one round trip on every instance in the deployment, all but one of
      // which then threw the answer away.
      // A camera that left this node is announced twice — once under its new owner, which is
      // dropped here, and once under the old one, which is not, so the subscription it leaves behind
      // is released without waiting for the sweep.
      if (!string.Equals(evt.ServerId, ownServerId, StringComparison.OrdinalIgnoreCase))
        return Task.FromResult(TopicResponseAction.Success);

      // Nothing named: the periodic sweep reconciles the whole set anyway, so ask it to run now.
      if (evt.CameraIds is not { Count: > 0 })
      {
        logger.LogDebug("Node-wide config change on '{ServerId}' — reconciling every camera", evt.ServerId);
        subscriptionService.NotifyAllCamerasChanged();
        return Task.FromResult(TopicResponseAction.Success);
      }

      foreach (var cameraId in evt.CameraIds)
      {
        connCache.Invalidate(cameraId);
        filterCache.Invalidate(cameraId);
        metadataGate.Invalidate(cameraId);
        subscriptionService.NotifyConfigChanged(cameraId);
      }
      logger.LogDebug("ONVIF caches invalidated for {Count} camera(s)", evt.CameraIds.Count);
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
