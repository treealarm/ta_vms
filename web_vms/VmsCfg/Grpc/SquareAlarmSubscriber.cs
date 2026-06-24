using System.Text.Json;
using Dapr.Messaging.PublishSubscribe;
using Google.Protobuf.WellKnownTypes;
using LeafletAlarmsGrpc;
using SquareIntegrationClient;

namespace VmsCfg;

// Only registered when Square integration is enabled (see Program.cs) — bridges VmsFs's
// retention-event-created notifications into a Square point-in-time event + last_alarm_ts
// property. VmsCfg stays the only connection point into Square per CLAUDE.md.
public sealed class SquareAlarmSubscriber(
  DaprPublishSubscribeClient pubSubClient,
  ISquareIntegration square,
  ILogger<SquareAlarmSubscriber> logger) : IHostedService, IDisposable
{
  private const string RetentionEventCreatedTopic = "retention-event-created";
  private static readonly string PubSubName =
    Environment.GetEnvironmentVariable("PUBSUB_NAME")
      ?? throw new InvalidOperationException("Required environment variable 'PUBSUB_NAME' is not set");

  private readonly CancellationTokenSource _cts = new();

  private record RetentionEventCreated(string CameraId, string? StreamName, DateTime AlarmStartTs);

  public Task StartAsync(CancellationToken cancellationToken)
  {
    _ = RunAsync(_cts.Token);
    return Task.CompletedTask;
  }

  private async Task RunAsync(CancellationToken ct)
  {
    try
    {
      await pubSubClient.SubscribeAsync(
        PubSubName,
        RetentionEventCreatedTopic,
        new DaprSubscriptionOptions(
          new MessageHandlingPolicy(TimeSpan.FromSeconds(10), TopicResponseAction.Retry)),
        HandleAsync,
        ct);
    }
    catch (OperationCanceledException) { }
    catch (Exception ex)
    {
      logger.LogError(ex, "SquareAlarmSubscriber error");
    }
  }

  private async Task<TopicResponseAction> HandleAsync(TopicMessage message, CancellationToken cancellationToken)
  {
    try
    {
      var evt = JsonSerializer.Deserialize<RetentionEventCreated>(message.Data.Span);
      if (evt is not null && !string.IsNullOrWhiteSpace(evt.CameraId))
      {
        await PushAlarmEventAsync(evt.CameraId, evt.AlarmStartTs, evt.StreamName);
      }
      return TopicResponseAction.Success;
    }
    catch
    {
      return TopicResponseAction.Retry;
    }
  }

  private async Task PushAlarmEventAsync(string cameraId, DateTime alarmStartTs, string? streamName)
  {
    var objectId = SquareCameraPush.ToObjectId(cameraId);

    await square.PushEvents(new EventsProto
    {
      Events =
      {
        new EventProto
        {
          Id = Guid.NewGuid().ToString(),
          ObjectId = objectId,
          Timestamp = Timestamp.FromDateTime(DateTime.SpecifyKind(alarmStartTs, DateTimeKind.Utc)),
          EventName = "alarm",
          EventPriority = 0,
          Param0 = streamName ?? "",
          Param1 = "",
        },
      },
    });

    await SquareCameraPush.PushPropertiesAsync(square, cameraId, new Dictionary<string, string>
    {
      ["last_alarm_ts"] = alarmStartTs.ToString("O"),
    });
  }

  public Task StopAsync(CancellationToken _)
  {
    _cts.Cancel();
    return Task.CompletedTask;
  }

  public void Dispose() => _cts.Dispose();
}
