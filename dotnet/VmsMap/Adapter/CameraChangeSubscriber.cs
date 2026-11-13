using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dapr.Messaging.PublishSubscribe;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using VmsLog;
using VmsMap.Clients;
using VmsMap.Domain;

namespace VmsMap.Adapter
{
  // The camera half of the adapter. VmsCfg already announces both changes for the services that
  // cache cameras; the map is one more subscriber and fetches the current state itself, so a
  // message that arrives late or out of order cannot install a stale name or parent.
  internal sealed class CameraChangeSubscriber : IHostedService, IDisposable
  {
    private const string ServerConfigChangedTopic = "server-config-changed";
    private const string CameraStateChangedTopic = "camera-state-changed";

    private static readonly string PubSubName =
      Environment.GetEnvironmentVariable("PUBSUB_NAME")
        ?? throw new InvalidOperationException("Required environment variable 'PUBSUB_NAME' is not set");

    private static readonly JsonSerializerOptions JsonOptions =
      new() { PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    private readonly DaprPublishSubscribeClient _pubSub;
    private readonly IServiceScopeFactory _scopes;
    private readonly ICameraCatalog _catalog;
    private readonly ICameraStateSource _state;
    private readonly ILogger<CameraChangeSubscriber> _logger;
    private readonly CancellationTokenSource _cts = new();

    // One reconcile at a time: a node-wide change and a per-camera event may arrive together, and
    // both read the same tree. Camera ids seen per server, so a node-wide reconcile can tell which
    // cameras disappeared.
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, HashSet<string>> _known = new(StringComparer.Ordinal);

    public CameraChangeSubscriber(
      DaprPublishSubscribeClient pubSub,
      IServiceScopeFactory scopes,
      ICameraCatalog catalog,
      ICameraStateSource state,
      ILogger<CameraChangeSubscriber> logger)
    {
      _pubSub = pubSub ?? throw new ArgumentNullException(nameof(pubSub));
      _scopes = scopes ?? throw new ArgumentNullException(nameof(scopes));
      _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
      _state = state ?? throw new ArgumentNullException(nameof(state));
      _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
      var options = new DaprSubscriptionOptions(
        new MessageHandlingPolicy(TimeSpan.FromSeconds(10), TopicResponseAction.Retry));

      _ = StreamingSubscriptions.KeepAsync(_pubSub, PubSubName, options,
        [
          new(ServerConfigChangedTopic, HandleConfigAsync),
          new(CameraStateChangedTopic, HandleStateAsync),
        ], _logger, _cts.Token);

      return Task.CompletedTask;
    }

    private sealed record ServerConfigChanged(string? ServerId, IReadOnlyList<string>? CameraIds);

    private async Task<TopicResponseAction> HandleConfigAsync(
      TopicMessage message, CancellationToken ct)
    {
      var evt = ReadPayload<ServerConfigChanged>(message);
      if (evt is null)
        return TopicResponseAction.Drop;

      try
      {
        await ReconcileAsync(evt.ServerId ?? string.Empty, evt.CameraIds ?? [], ct);
        return TopicResponseAction.Success;
      }
      catch (OperationCanceledException) when (ct.IsCancellationRequested)
      {
        return TopicResponseAction.Retry;
      }
      catch (Exception ex)
      {
        _logger.LogWarning(ex, "Map camera reconcile failed for server '{ServerId}'", evt.ServerId);
        return TopicResponseAction.Retry;
      }
    }

    private async Task ReconcileAsync(
      string serverId, IReadOnlyList<string> cameraIds, CancellationToken ct)
    {
      await _gate.WaitAsync(ct);
      try
      {
        using var scope = _scopes.CreateScope();
        var nodes = scope.ServiceProvider.GetRequiredService<DeviceNodeService>();
        var diagrams = scope.ServiceProvider.GetRequiredService<IDeviceDiagramService>();

        if (cameraIds.Count == 0)
        {
          // Something node-wide changed: a MediaMTX row, a pairing, a camera the event did not
          // name. Re-read the node's camera set and remove what is no longer there.
          var cameras = await _catalog.ListAsync(serverId, ct);
          var current = cameras.Select(c => c.CameraId).ToHashSet(StringComparer.Ordinal);

          foreach (var camera in cameras)
            await nodes.EnsureCameraAsync(camera, ct);

          if (_known.TryGetValue(serverId, out var known))
          {
            foreach (var gone in known.Except(current, StringComparer.Ordinal).ToList())
              await nodes.RemoveCameraAsync(gone, ct);
          }

          _known[serverId] = current;
          return;
        }

        foreach (var cameraId in cameraIds)
        {
          var camera = await _catalog.GetAsync(cameraId, ct);
          if (camera is null)
          {
            await nodes.RemoveCameraAsync(cameraId, ct);
            Forget(cameraId);
          }
          else
          {
            await nodes.EnsureCameraAsync(camera, ct);

            // A camera the event names may be new to a template that already exists. The node-wide
            // branch below does not do this: it runs on every MediaMTX or pairing change, and a
            // diagram pass over every camera on the node is a full table scan for an event that
            // cannot create one. The periodic snapshot reconciles those.
            await diagrams.EnsureAsync(
              DeviceObjectId.CameraSource, cameraId, IDeviceDiagramService.DefaultSlot, ct);

            Remember(camera.ServerId, cameraId);
          }
        }
      }
      finally
      {
        _gate.Release();
      }
    }

    private async Task<TopicResponseAction> HandleStateAsync(
      TopicMessage message, CancellationToken ct)
    {
      var cameraId = ReadPayload<string>(message);
      if (string.IsNullOrWhiteSpace(cameraId))
        return TopicResponseAction.Drop;

      try
      {
        var armed = await _state.GetArmedAsync(cameraId, ct);

        using var scope = _scopes.CreateScope();
        var nodes = scope.ServiceProvider.GetRequiredService<DeviceNodeService>();
        await nodes.SetPropertyAsync(
          cameraId, "armed", armed.ToString(System.Globalization.CultureInfo.InvariantCulture), ct);

        // A camera that becomes armed with a sensor already in its trigger position must raise the
        // alarm now: its flag is what an input on `inherit` resolves to, and no settings event is
        // coming to re-check it.
        var sensorAlarms = scope.ServiceProvider.GetRequiredService<SensorAlarmService>();
        await sensorAlarms.ReconcileCameraAsync(cameraId, ct);

        return TopicResponseAction.Success;
      }
      catch (OperationCanceledException) when (ct.IsCancellationRequested)
      {
        return TopicResponseAction.Retry;
      }
      catch (Exception ex)
      {
        // A state push is a convenience for the tree; retrying it forever would only delay the
        // next message. The next change will carry the current value.
        _logger.LogWarning(ex, "Map armed property push failed for camera '{CameraId}'", cameraId);
        return TopicResponseAction.Success;
      }
    }

    private void Remember(string serverId, string cameraId)
    {
      var key = serverId ?? string.Empty;
      if (!_known.TryGetValue(key, out var set))
        _known[key] = set = new HashSet<string>(StringComparer.Ordinal);
      set.Add(cameraId);
    }

    private void Forget(string cameraId)
    {
      foreach (var set in _known.Values)
        set.Remove(cameraId);
    }

    private T? ReadPayload<T>(TopicMessage message)
    {
      try
      {
        return JsonSerializer.Deserialize<T>(message.Data.Span, JsonOptions);
      }
      catch (JsonException ex)
      {
        _logger.LogWarning(ex, "Dropping a malformed map-adapter message");
        return default;
      }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
      _cts.Cancel();
      return Task.CompletedTask;
    }

    public void Dispose()
    {
      _cts.Dispose();
      _gate.Dispose();
    }
  }
}
