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

namespace VmsMap.Adapter
{
  // retention-event-created is addressing only: VmsFs says which camera and when the episode
  // started, and the map reads the episode back to learn its trigger and projected end. The event
  // is published after the row is written, so a missing row is a race worth retrying, not an empty
  // alarm.
  internal sealed class RetentionEventSubscriber : IHostedService, IDisposable
  {
    private const string RetentionEventCreatedTopic = "retention-event-created";

    private static readonly string PubSubName =
      Environment.GetEnvironmentVariable("PUBSUB_NAME")
        ?? throw new InvalidOperationException("Required environment variable 'PUBSUB_NAME' is not set");

    private static readonly JsonSerializerOptions JsonOptions =
      new() { PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    private readonly DaprPublishSubscribeClient _pubSub;
    private readonly IServiceScopeFactory _scopes;
    private readonly ICameraCatalog _catalog;
    private readonly IRetentionEventSource _retention;
    private readonly ILogger<RetentionEventSubscriber> _logger;
    private readonly CancellationTokenSource _cts = new();

    public RetentionEventSubscriber(
      DaprPublishSubscribeClient pubSub,
      IServiceScopeFactory scopes,
      ICameraCatalog catalog,
      IRetentionEventSource retention,
      ILogger<RetentionEventSubscriber> logger)
    {
      _pubSub = pubSub ?? throw new ArgumentNullException(nameof(pubSub));
      _scopes = scopes ?? throw new ArgumentNullException(nameof(scopes));
      _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
      _retention = retention ?? throw new ArgumentNullException(nameof(retention));
      _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
      var options = new DaprSubscriptionOptions(
        new MessageHandlingPolicy(TimeSpan.FromSeconds(10), TopicResponseAction.Retry));

      _ = StreamingSubscriptions.KeepAsync(_pubSub, PubSubName, options,
        [new(RetentionEventCreatedTopic, HandleAsync)], _logger, _cts.Token);

      return Task.CompletedTask;
    }

    private sealed record RetentionEventCreated(
      string CameraId, string? StreamName, DateTime AlarmStartTs);

    private async Task<TopicResponseAction> HandleAsync(
      TopicMessage message, CancellationToken ct)
    {
      RetentionEventCreated? evt;
      try
      {
        evt = JsonSerializer.Deserialize<RetentionEventCreated>(message.Data.Span, JsonOptions);
      }
      catch (JsonException ex)
      {
        _logger.LogWarning(ex, "Dropping a malformed retention-event-created message");
        return TopicResponseAction.Drop;
      }

      if (evt is null || string.IsNullOrWhiteSpace(evt.CameraId))
        return TopicResponseAction.Drop;

      try
      {
        var camera = await _catalog.GetAsync(evt.CameraId, ct);
        if (camera is null)
        {
          // A camera removed between the episode and this message has no node to carry the state.
          return TopicResponseAction.Drop;
        }

        var episode = await _retention.FindAsync(evt.CameraId, evt.AlarmStartTs, ct);
        if (episode is null)
          return TopicResponseAction.Retry;

        using var scope = _scopes.CreateScope();
        var nodes = scope.ServiceProvider.GetRequiredService<CameraNodeService>();
        await nodes.EnsureCameraAsync(camera, ct);

        var alarms = scope.ServiceProvider.GetRequiredService<AlarmService>();
        await alarms.RecordAsync(evt.CameraId, episode, ct);

        return TopicResponseAction.Success;
      }
      catch (OperationCanceledException) when (ct.IsCancellationRequested)
      {
        return TopicResponseAction.Retry;
      }
      catch (Exception ex)
      {
        _logger.LogWarning(ex,
          "Retention event for camera '{CameraId}' could not be recorded", evt.CameraId);
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
