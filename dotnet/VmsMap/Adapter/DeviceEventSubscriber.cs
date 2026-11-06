using System;
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
  // The device half of the adapter: the raw stream from VmsOnvif, its periodic snapshot, and the
  // settings that give the parts their names and arming.
  internal sealed class DeviceEventSubscriber : IHostedService, IDisposable
  {
    private const string DeviceEventTopic = "device-event";
    private const string DeviceSnapshotTopic = "device-snapshot";
    private const string DeviceSettingsChangedTopic = "device-settings-changed";

    private static readonly string PubSubName =
      Environment.GetEnvironmentVariable("PUBSUB_NAME")
        ?? throw new InvalidOperationException("Required environment variable 'PUBSUB_NAME' is not set");

    private readonly DaprPublishSubscribeClient _pubSub;
    private readonly IServiceScopeFactory _scopes;
    private readonly IDeviceSettingsSource _settings;
    private readonly ILogger<DeviceEventSubscriber> _logger;
    private readonly CancellationTokenSource _cts = new();

    public DeviceEventSubscriber(
      DaprPublishSubscribeClient pubSub,
      IServiceScopeFactory scopes,
      IDeviceSettingsSource settings,
      ILogger<DeviceEventSubscriber> logger)
    {
      _pubSub = pubSub;
      _scopes = scopes;
      _settings = settings;
      _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
      var options = new DaprSubscriptionOptions(
        new MessageHandlingPolicy(TimeSpan.FromSeconds(10), TopicResponseAction.Retry));

      _ = StreamingSubscriptions.KeepAsync(_pubSub, PubSubName, options,
        [
          new(DeviceEventTopic, HandleEventAsync),
          new(DeviceSnapshotTopic, HandleSnapshotAsync),
          new(DeviceSettingsChangedTopic, HandleSettingsChangedAsync),
        ], _logger, _cts.Token);

      return Task.CompletedTask;
    }

    private async Task<TopicResponseAction> HandleEventAsync(
      TopicMessage message, CancellationToken ct)
    {
      var evt = ReadPayload<DeviceEventMessage>(message);
      if (evt is null || string.IsNullOrWhiteSpace(evt.SourceId) || string.IsNullOrWhiteSpace(evt.Token))
        return TopicResponseAction.Drop;

      try
      {
        using var scope = _scopes.CreateScope();
        var nodes = scope.ServiceProvider.GetRequiredService<DeviceNodeService>();

        var objectId = await nodes.EnsureAsync(
          evt.SourceKind ?? DeviceObjectId.CameraSource,
          evt.SourceId!,
          evt.PartKind ?? string.Empty,
          evt.Token!,
          ct);

        // The camera is gone from VmsCfg: there is no node to carry the state.
        if (objectId is null)
          return TopicResponseAction.Drop;

        var writer = scope.ServiceProvider.GetRequiredService<DeviceEventWriter>();
        await writer.ApplyAsync(objectId, evt, ct);
        return TopicResponseAction.Success;
      }
      catch (OperationCanceledException) when (ct.IsCancellationRequested)
      {
        return TopicResponseAction.Retry;
      }
      catch (Exception ex)
      {
        _logger.LogWarning(ex,
          "device-event for camera '{CameraId}' part {Kind}/{Token} could not be applied",
          evt.SourceId, evt.PartKind, evt.Token);
        return TopicResponseAction.Retry;
      }
    }

    private async Task<TopicResponseAction> HandleSnapshotAsync(
      TopicMessage message, CancellationToken ct)
    {
      var snapshot = ReadPayload<DeviceSnapshotMessage>(message);
      if (snapshot is null || string.IsNullOrWhiteSpace(snapshot.SourceId))
        return TopicResponseAction.Drop;

      try
      {
        using var scope = _scopes.CreateScope();
        var reconciler = scope.ServiceProvider.GetRequiredService<DeviceEventReconciler>();
        await reconciler.ApplyAsync(snapshot, ct);
        return TopicResponseAction.Success;
      }
      catch (OperationCanceledException) when (ct.IsCancellationRequested)
      {
        return TopicResponseAction.Retry;
      }
      catch (Exception ex)
      {
        _logger.LogWarning(ex,
          "device-snapshot for camera '{CameraId}' could not be reconciled", snapshot.SourceId);
        return TopicResponseAction.Retry;
      }
    }

    private sealed record DeviceSettingsChanged(string? CameraId);

    private async Task<TopicResponseAction> HandleSettingsChangedAsync(
      TopicMessage message, CancellationToken ct)
    {
      var evt = ReadPayload<DeviceSettingsChanged>(message);
      if (evt is null || string.IsNullOrWhiteSpace(evt.CameraId))
        return TopicResponseAction.Drop;

      // The signal only: the version key is the truth, and the cache checks it on its own clock.
      _settings.Invalidate(evt.CameraId!);

      try
      {
        using var scope = _scopes.CreateScope();
        var nodes = scope.ServiceProvider.GetRequiredService<DeviceNodeService>();
        await nodes.RefreshPartNamesAsync(evt.CameraId!, ct);
        return TopicResponseAction.Success;
      }
      catch (OperationCanceledException) when (ct.IsCancellationRequested)
      {
        return TopicResponseAction.Retry;
      }
      catch (Exception ex)
      {
        _logger.LogWarning(ex,
          "Renaming the parts of camera '{CameraId}' after a settings change failed", evt.CameraId);
        // The next event or snapshot carries the name again; retrying forever delays the stream.
        return TopicResponseAction.Success;
      }
    }

    private T? ReadPayload<T>(TopicMessage message)
    {
      try
      {
        return JsonSerializer.Deserialize<T>(message.Data.Span, DevicePayload.JsonOptions);
      }
      catch (JsonException ex)
      {
        _logger.LogWarning(ex, "Dropping a malformed device message");
        return default;
      }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
      _cts.Cancel();
      return Task.CompletedTask;
    }

    public void Dispose() => _cts.Dispose();
  }
}
