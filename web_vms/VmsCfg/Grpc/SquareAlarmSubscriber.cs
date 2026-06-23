using System.Text.Json;
using Dapr.Messaging.PublishSubscribe;
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
        await square.PushAlarmEvent(evt.CameraId, evt.AlarmStartTs, "alarm", evt.StreamName);
      }
      return TopicResponseAction.Success;
    }
    catch
    {
      return TopicResponseAction.Retry;
    }
  }

  public Task StopAsync(CancellationToken _)
  {
    _cts.Cancel();
    return Task.CompletedTask;
  }

  public void Dispose() => _cts.Dispose();
}
