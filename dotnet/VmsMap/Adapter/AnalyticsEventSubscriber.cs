using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dapr.Messaging.PublishSubscribe;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using VmsLog;
using VmsMap.Clients;
using VmsMap.Db;
using VmsMap.Domain;

namespace VmsMap.Adapter
{
  /// <summary>
  /// A persisted detection from VmsAnalyticsSingleton, as it arrives on <c>analytics-event</c>.
  /// One message per detection; the event id is derived from <c>detection_id</c>, so a redelivery
  /// is the same journal row.
  /// </summary>
  internal sealed record AnalyticsEventMessage(
    long DetectionId,
    string CameraId,
    string Kind,
    float Confidence,
    string? ObjectId,
    DateTimeOffset DetectedAt);

  /// <summary>
  /// The analytics half of the adapter: a persisted detection becomes an <c>analytics</c> journal
  /// row — but only while an enabled subscription asks for one. The gate is not an optimisation:
  /// at a hundred thousand cameras every persisted detection would otherwise turn the journal into
  /// a detection feed and the router into a loop that matches nothing.
  /// </summary>
  internal sealed class AnalyticsEventSubscriber : IHostedService, IDisposable
  {
    private const string AnalyticsEventTopic = "analytics-event";
    private const string SubscriptionsChangedTopic = "event-subscriptions-changed";
    // The name a subscription selects on, and the row's event_name. One constant, because the gate
    // and the journal row must agree: otherwise rows nobody reacts to would be written.
    private const string JournalEventName = "analytics";

    private static readonly string PubSubName =
      Environment.GetEnvironmentVariable("PUBSUB_NAME")
        ?? throw new InvalidOperationException("Required environment variable 'PUBSUB_NAME' is not set");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
      PropertyNameCaseInsensitive = true,
      PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly DaprPublishSubscribeClient _pubSub;
    private readonly IServiceScopeFactory _scopes;
    private readonly AnalyticsSubscriptionGate _gate;
    private readonly ILogger<AnalyticsEventSubscriber> _logger;
    private readonly CancellationTokenSource _cts = new();

    public AnalyticsEventSubscriber(
      DaprPublishSubscribeClient pubSub,
      IServiceScopeFactory scopes,
      AnalyticsSubscriptionGate gate,
      ILogger<AnalyticsEventSubscriber> logger)
    {
      _pubSub = pubSub ?? throw new ArgumentNullException(nameof(pubSub));
      _scopes = scopes ?? throw new ArgumentNullException(nameof(scopes));
      _gate = gate ?? throw new ArgumentNullException(nameof(gate));
      _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
      var options = new DaprSubscriptionOptions(
        new MessageHandlingPolicy(TimeSpan.FromSeconds(10), TopicResponseAction.Retry));

      _ = StreamingSubscriptions.KeepAsync(_pubSub, PubSubName, options,
        [
          new(AnalyticsEventTopic, HandleAnalyticsAsync),
          new(SubscriptionsChangedTopic, HandleSubscriptionsChangedAsync),
        ], _logger, _cts.Token);

      return Task.CompletedTask;
    }

    private async Task<TopicResponseAction> HandleAnalyticsAsync(
      TopicMessage message, CancellationToken ct)
    {
      var evt = ReadPayload(message);
      if (evt is null || evt.DetectionId <= 0 || !Guid.TryParse(evt.CameraId, out _))
        return TopicResponseAction.Drop;

      try
      {
        // Checked per message at processing time: enabling a subscription does not reach a
        // detection already handled, and disabling one does not retract a row already written.
        if (!await _gate.HasEnabledAsync(JournalEventName, ct))
          return TopicResponseAction.Success;

        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<VmsMapDbContext>();

        var objectId = CameraObjectId.For(evt.CameraId);
        var objectGuid = Utils.ConvertObjectIdToGuid(objectId);
        if (objectGuid is null)
          return TopicResponseAction.Drop;

        // The camera node is created by the adapter from config and at startup; the check is one
        // primary-key read, so a detection does not pay for the full ensure on every message.
        if (!await db.Markers.AsNoTracking().AnyAsync(m => m.id == objectGuid.Value, ct))
        {
          var catalog = scope.ServiceProvider.GetRequiredService<ICameraCatalog>();
          var camera = await catalog.GetAsync(evt.CameraId, ct);
          // The camera is gone from VmsCfg: there is no node, and nothing to attach the event to.
          if (camera is null)
            return TopicResponseAction.Success;

          await scope.ServiceProvider.GetRequiredService<IDeviceNodeService>()
            .EnsureCameraAsync(camera, ct);
        }

        var writer = scope.ServiceProvider.GetRequiredService<EventsWriter>();
        var written = await writer.WriteAsync([ToEvent(objectId, evt)], ct);

        if (written.Count == 0)
          _logger.LogDebug(
            "Analytics detection {DetectionId} is already in the journal — redelivery", evt.DetectionId);

        return TopicResponseAction.Success;
      }
      catch (OperationCanceledException) when (ct.IsCancellationRequested)
      {
        return TopicResponseAction.Retry;
      }
      catch (Exception ex)
      {
        _logger.LogWarning(ex,
          "analytics-event for camera '{CameraId}' detection {DetectionId} could not be applied",
          evt.CameraId, evt.DetectionId);
        return TopicResponseAction.Retry;
      }
    }

    private Task<TopicResponseAction> HandleSubscriptionsChangedAsync(
      TopicMessage message, CancellationToken ct)
    {
      // The signal only: the version key is the truth, and the gate checks it on its own clock.
      _gate.Invalidate();
      return Task.FromResult(TopicResponseAction.Success);
    }

    internal static EventDTO ToEvent(string objectId, AnalyticsEventMessage evt)
    {
      var props = new List<ObjExtraPropertyDTO>
      {
        Text("camera_id", evt.CameraId),
        // The detection's own kind: `kinds` in a selector is about device parts, so analytics
        // filters by `props.kind` instead.
        Text("kind", evt.Kind),
        Text("confidence", evt.Confidence.ToString("0.######", CultureInfo.InvariantCulture)),
        Text("detection_id", evt.DetectionId.ToString(CultureInfo.InvariantCulture)),
      };

      if (!string.IsNullOrWhiteSpace(evt.ObjectId))
        props.Add(Text("reid_object_id", evt.ObjectId));

      return new EventDTO
      {
        id = EventIds.ForAnalytics(evt.DetectionId),
        object_id = objectId,
        event_name = JournalEventName,
        event_priority = 0,
        timestamp = evt.DetectedAt.UtcDateTime,
        param0 = evt.Kind,
        extra_props = props,
      };
    }

    private static ObjExtraPropertyDTO Text(string name, string value) => new()
    {
      prop_name = name,
      str_val = value,
    };

    private AnalyticsEventMessage? ReadPayload(TopicMessage message)
    {
      try
      {
        return JsonSerializer.Deserialize<AnalyticsEventMessage>(message.Data.Span, JsonOptions);
      }
      catch (JsonException ex)
      {
        _logger.LogWarning(ex, "Dropping a malformed analytics message");
        return null;
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
