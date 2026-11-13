using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dapr.Messaging.PublishSubscribe;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using VmsLog;
using VmsMap.Domain.Arming;
using VmsMap.Outbox;

namespace VmsMap.Adapter
{
  /// <summary>
  /// A container's arming changed and the sensors already in their trigger position have to be
  /// re-checked. The signal travels through the outbox row written in the same transaction as the
  /// property, so a crash between the commit and this handler leaves the row for the publisher to
  /// find rather than a zone that says "on guard" over an alarm nobody raised.
  /// <para>
  /// Delivery is one replica's: the re-check is idempotent, and every replica reads the same map.
  /// The device snapshot remains the backstop if every retry is lost.
  /// </para>
  /// </summary>
  internal sealed class ArmingChangedSubscriber : IHostedService, IDisposable
  {
    private static readonly string PubSubName =
      Environment.GetEnvironmentVariable("PUBSUB_NAME")
        ?? throw new InvalidOperationException("Required environment variable 'PUBSUB_NAME' is not set");

    private readonly DaprPublishSubscribeClient _pubSub;
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<ArmingChangedSubscriber> _logger;
    private readonly CancellationTokenSource _cts = new();

    public ArmingChangedSubscriber(
      DaprPublishSubscribeClient pubSub,
      IServiceScopeFactory scopes,
      ILogger<ArmingChangedSubscriber> logger)
    {
      _pubSub = pubSub;
      _scopes = scopes;
      _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
      var options = new DaprSubscriptionOptions(
        new MessageHandlingPolicy(TimeSpan.FromSeconds(10), TopicResponseAction.Retry));

      _ = StreamingSubscriptions.KeepAsync(_pubSub, PubSubName, options,
        [new(MapTopics.ArmingChanged, HandleAsync)], _logger, _cts.Token);

      return Task.CompletedTask;
    }

    private sealed record ArmingChanged(string? ObjectId);

    private async Task<TopicResponseAction> HandleAsync(
      TopicMessage message, CancellationToken ct)
    {
      ArmingChanged? changed;
      try
      {
        changed = JsonSerializer.Deserialize<ArmingChanged>(
          message.Data.Span, DevicePayload.JsonOptions);
      }
      catch (JsonException ex)
      {
        _logger.LogWarning(ex, "Dropping a malformed arming message");
        return TopicResponseAction.Drop;
      }

      if (changed is null || string.IsNullOrWhiteSpace(changed.ObjectId))
        return TopicResponseAction.Drop;

      try
      {
        using var scope = _scopes.CreateScope();

        // The zone's answer is now the cameras' flag as well: VmsOnvif and the routing gateway
        // read that flag and cannot see the map.
        var cameras = scope.ServiceProvider.GetRequiredService<CameraArmingReconciler>();
        await cameras.ReconcileSubtreeAsync(changed.ObjectId!, ct);

        var alarms = scope.ServiceProvider.GetRequiredService<SensorAlarmService>();
        await alarms.ReconcileSpaceAsync(changed.ObjectId!, ct);
        return TopicResponseAction.Success;
      }
      catch (OperationCanceledException) when (ct.IsCancellationRequested)
      {
        return TopicResponseAction.Retry;
      }
      catch (Exception ex)
      {
        _logger.LogWarning(ex,
          "Re-checking the sensors under '{ObjectId}' after its arming changed failed",
          changed.ObjectId);
        return TopicResponseAction.Retry;
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
