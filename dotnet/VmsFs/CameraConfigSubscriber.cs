using Dapr.Messaging.PublishSubscribe;
using System.Text.Json;
using VmsLog;

namespace VmsFs;

public sealed class CameraConfigSubscriber(
  DaprPublishSubscribeClient pubSubClient,
  CameraConfigCache configCache,
  CameraSetupCache setupCache,
  ILogger<CameraConfigSubscriber> logger) : IHostedService, IDisposable
{
  private const string ServerConfigChangedTopic = "server-config-changed";
  private static readonly string PubSubName =
    Environment.GetEnvironmentVariable("PUBSUB_NAME")
      ?? throw new InvalidOperationException("Required environment variable 'PUBSUB_NAME' is not set");

  private readonly CancellationTokenSource _cts = new();

  public Task StartAsync(CancellationToken cancellationToken)
  {
    var options = new DaprSubscriptionOptions(
      new MessageHandlingPolicy(TimeSpan.FromSeconds(10), TopicResponseAction.Retry));
    _ = StreamingSubscriptions.KeepAsync(pubSubClient, PubSubName, options,
      [new(ServerConfigChangedTopic, HandleAsync)], logger, _cts.Token);
    return Task.CompletedTask;
  }

  // Wire shape of server-config-changed; VmsCfg owns it. Empty CameraIds means everything on the
  // node. Not filtered by server here: dropping a cache entry for a camera this instance does not
  // own costs nothing, and the archive follows the camera when one is reassigned.
  private sealed record ServerConfigChanged(string? ServerId, IReadOnlyList<string>? CameraIds);

  private static readonly JsonSerializerOptions JsonOptions =
    new() { PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

  private Task<TopicResponseAction> HandleAsync(TopicMessage message, CancellationToken _)
  {
    try
    {
      var evt = JsonSerializer.Deserialize<ServerConfigChanged>(message.Data.Span, JsonOptions);
      if (evt is null)
        return Task.FromResult(TopicResponseAction.Drop);

      if (evt.CameraIds is not { Count: > 0 })
      {
        configCache.InvalidateAll();
        setupCache.InvalidateAll();
        logger.LogDebug("Config caches invalidated for server '{ServerId}'", evt.ServerId);
        return Task.FromResult(TopicResponseAction.Success);
      }

      foreach (var cameraId in evt.CameraIds)
      {
        configCache.Invalidate(cameraId);
        setupCache.Invalidate(cameraId);
      }
      logger.LogDebug("Config caches invalidated for {Count} camera(s)", evt.CameraIds.Count);
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
