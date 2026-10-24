using System.Text.Json;
using Dapr.Messaging.PublishSubscribe;
using VmsLog;

namespace VmsUtils;

/// Keeps MediaMtxServerCache honest by listening to the config-change events VmsCfg already
/// publishes, rather than re-reading the assignment on every poll.
///
/// server-config-changed names the cameras that changed, so only those entries are dropped. An empty
/// camera list means the node itself changed — a MediaMTX row added, edited or removed, where a new
/// host or api_port invalidates every entry pointing at that server — so there the whole cache goes.
/// That is rare, and costs one re-resolve per camera on the next poll.
public sealed class MediaMtxConfigSubscriber(
  DaprPublishSubscribeClient pubSubClient,
  MediaMtxServerCache serverCache,
  ILogger<MediaMtxConfigSubscriber> logger) : IHostedService, IDisposable
{
  private const string ServerConfigChangedTopic = "server-config-changed";

  private static readonly string PubSubName =
    Environment.GetEnvironmentVariable("PUBSUB_NAME")
      ?? throw new InvalidOperationException("Required environment variable 'PUBSUB_NAME' is not set");

  private readonly CancellationTokenSource _cts = new();

  public Task StartAsync(CancellationToken cancellationToken)
  {
    var options = new DaprSubscriptionOptions(
      new MessageHandlingPolicy(TimeSpan.FromSeconds(10), TopicResponseAction.Drop));
    _ = StreamingSubscriptions.KeepAsync(pubSubClient, PubSubName, options,
      [new(ServerConfigChangedTopic, HandleServerConfigChangedAsync)], logger, _cts.Token);
    return Task.CompletedTask;
  }

  // Wire shape of server-config-changed; VmsCfg owns it. Empty CameraIds means everything on the
  // node — the only case that still justifies dropping the whole map, which is what every event used
  // to do because none of them said who was affected.
  private sealed record ServerConfigChanged(string? ServerId, IReadOnlyList<string>? CameraIds);

  private static readonly JsonSerializerOptions JsonOptions =
    new() { PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

  private Task<TopicResponseAction> HandleServerConfigChangedAsync(TopicMessage message, CancellationToken _)
  {
    var evt = ReadEvent(message);
    if (evt is null)
      return Task.FromResult(TopicResponseAction.Drop);

    if (evt.CameraIds is not { Count: > 0 })
      serverCache.Clear();
    else
      foreach (var cameraId in evt.CameraIds)
        serverCache.Invalidate(cameraId);

    return Task.FromResult(TopicResponseAction.Success);
  }

  private ServerConfigChanged? ReadEvent(TopicMessage message)
  {
    try
    {
      return JsonSerializer.Deserialize<ServerConfigChanged>(message.Data.Span, JsonOptions);
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
