using System.Text.Json;
using Dapr.Messaging.PublishSubscribe;
using VmsLog;
using VmsPtzTrackingSingleton.Geo;

namespace VmsPtzTrackingSingleton;

/// <summary>
/// Keeps the caches and the running sessions honest when VmsCfg changes a camera, or VmsMap a
/// space, a landmark, a placement or a calibration.
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
  // The map's configuration changed: a space, a plan, a landmark, a placement or a calibration.
  // camera_ids names the cameras whose model did; an empty list means a space or a landmark moved,
  // so any camera's cached model may be wrong.
  private const string MapConfigChangedTopic = "map-config-changed";

  private static readonly string PubSubName =
    Environment.GetEnvironmentVariable("PUBSUB_NAME")
      ?? throw new InvalidOperationException("Required environment variable 'PUBSUB_NAME' is not set");

  private readonly CancellationTokenSource _cts = new();

  // Wire shape of server-config-changed; VmsCfg owns it. camera_ids is addressing, not state: it
  // narrows what to re-check, and the current configuration is then fetched rather than read out
  // of the payload, so a late or out-of-order event cannot install a stale answer.
  private sealed record ServerConfigChanged(string? ServerId, IReadOnlyList<string>? CameraIds);

  // Wire shape of map-config-changed; VmsMap owns it. Same addressing rule.
  private sealed record MapConfigChanged(
    IReadOnlyList<string>? ObjectIds, IReadOnlyList<string>? CameraIds);

  private static readonly JsonSerializerOptions JsonOptions =
    new() { PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

  public Task StartAsync(CancellationToken cancellationToken)
  {
    var options = new DaprSubscriptionOptions(
      new MessageHandlingPolicy(TimeSpan.FromSeconds(10), TopicResponseAction.Retry));
    _ = StreamingSubscriptions.KeepAsync(pubSubClient, PubSubName, options,
      [new(ServerConfigChangedTopic, HandleAsync), new(MapConfigChangedTopic, HandleMapConfigAsync)],
      logger, _cts.Token);
    return Task.CompletedTask;
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

  // The event is addressing: the cameras it names have their models dropped, and an empty list
  // means a space or a landmark moved -- which may underlie any camera's calibration -- so every
  // model is dropped. A malformed event is treated the same way: a dropped model costs one fetch,
  // a stale one costs a wrong aim.
  private Task<TopicResponseAction> HandleMapConfigAsync(TopicMessage message, CancellationToken ct)
  {
    IReadOnlyList<string> cameraIds = [];
    try
    {
      var evt = JsonSerializer.Deserialize<MapConfigChanged>(message.Data.Span, JsonOptions);
      cameraIds = evt?.CameraIds ?? [];
    }
    catch (Exception ex)
    {
      logger.LogWarning(ex, "Could not read a map-config-changed event; dropping every spatial model");
    }

    spatialCache.Invalidate(cameraIds);
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
