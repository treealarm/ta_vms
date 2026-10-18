using System.Text.Json;
using Dapr.Messaging.PublishSubscribe;
using VmsPtzTrackingSingleton.Geo;

namespace VmsPtzTrackingSingleton;

/// <summary>
/// Keeps the caches and the running sessions honest when VmsCfg changes a camera, a space or a
/// landmark.
/// </summary>
/// <remarks>
/// Pub/sub invalidation is the right mechanism here precisely because this service is a singleton:
/// a Dapr topic delivers each event to one instance of an app id, and there is only ever one. (The
/// same cache in web_srv.Server has to use a version key in the state store instead, because that
/// service runs in several replicas — see CLAUDE.md.)
/// </remarks>
public sealed class CameraConfigSubscriber(
  DaprPublishSubscribeClient pubSubClient,
  CameraPtzRouter router,
  PtzCalibrationCache calibrationCache,
  SlaveBindingCache bindingCache,
  SpatialModelCache spatialCache,
  TrackingReconciler reconciler,
  ILogger<CameraConfigSubscriber> logger) : IHostedService, IDisposable
{
  private const string ServerConfigChangedTopic = "server-config-changed";
  // A space or a landmark changed. Names no camera: any number of them may be calibrated against
  // that landmark, or stand in that space.
  private const string SpaceConfigChangedTopic = "space-config-changed";

  private static readonly string PubSubName =
    Environment.GetEnvironmentVariable("PUBSUB_NAME")
      ?? throw new InvalidOperationException("Required environment variable 'PUBSUB_NAME' is not set");

  private readonly CancellationTokenSource _cts = new();

  // Wire shape of server-config-changed; VmsCfg owns it. camera_ids is addressing, not state: it
  // narrows what to re-check, and the current configuration is then fetched rather than read out
  // of the payload, so a late or out-of-order event cannot install a stale answer.
  private sealed record ServerConfigChanged(string? ServerId, IReadOnlyList<string>? CameraIds);

  private static readonly JsonSerializerOptions JsonOptions =
    new() { PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

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
        pubSubClient.SubscribeAsync(PubSubName, ServerConfigChangedTopic, options, HandleAsync, ct),
        pubSubClient.SubscribeAsync(PubSubName, SpaceConfigChangedTopic, options, HandleSpaceAsync, ct));
    }
    catch (OperationCanceledException) { }
    catch (Exception ex)
    {
      logger.LogError(ex, "CameraConfigSubscriber error");
    }
  }

  // Not async: every step here is a cache drop or a signal, and the reconcile it asks for
  // deliberately happens on the reconciler's own task rather than on the bus's.
  private Task<TopicResponseAction> HandleAsync(TopicMessage message, CancellationToken ct)
  {
    try
    {
      var evt = JsonSerializer.Deserialize<ServerConfigChanged>(message.Data.Span, JsonOptions);
      if (evt is null)
        return Task.FromResult(TopicResponseAction.Drop);

      var cameraIds = evt.CameraIds ?? [];
      router.Invalidate(cameraIds);
      calibrationCache.Invalidate(cameraIds);
      bindingCache.Invalidate(cameraIds);
      spatialCache.Invalidate(cameraIds);
      // Drop the stale answers first, then ask for a reconcile: the reconciler re-reads through
      // these same caches, and doing it the other way round would have it re-install what was just
      // thrown away. The policy itself is not cached — the reconciler always reads it fresh.
      //
      // Triggering rather than awaiting is deliberate. The handler runs under a ten-second
      // delivery policy, and a reconcile talks to VmsCfg and to every camera's ONVIF host; making
      // the bus wait for that turns a slow camera into redelivered events.
      reconciler.Trigger();
      return Task.FromResult(TopicResponseAction.Success);
    }
    catch (Exception ex)
    {
      logger.LogWarning(ex, "Could not handle a server-config-changed event");
      return Task.FromResult(TopicResponseAction.Retry);
    }
  }

  // The payload ({"space_id"}) is not read: which cameras a moved landmark affects is not in it, and
  // working that out would be a VmsCfg round trip for every model held. Dropping them all costs one
  // fetch per slave pair on the next reconcile, which is what it is about to do anyway.
  private Task<TopicResponseAction> HandleSpaceAsync(TopicMessage message, CancellationToken ct)
  {
    spatialCache.Invalidate([]);
    reconciler.Trigger();
    return Task.FromResult(TopicResponseAction.Success);
  }

  public Task StopAsync(CancellationToken cancellationToken)
  {
    _cts.Cancel();
    return Task.CompletedTask;
  }

  public void Dispose() => _cts.Dispose();
}
