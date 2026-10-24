using Dapr.Messaging.PublishSubscribe;
using VmsLog;

namespace VmsSingleton;

/// Wakes the schedule ticker when VmsCfg reports a schedule edit.
///
/// The ticker sleeps until the next known boundary, so without this a window created to open in a
/// few seconds would wait for the sleep to expire. In VmsCfg this was an in-process semaphore; now
/// that the two live in different services it is a topic — which works precisely because this
/// service has its own Dapr app id, and therefore its own subscription.
public sealed class ScheduleChangeSignal(
  DaprPublishSubscribeClient pubSubClient,
  ILogger<ScheduleChangeSignal> logger) : IHostedService, IDisposable
{
  private const string Topic = "recording-schedule-changed";
  private static readonly string PubSubName =
    Environment.GetEnvironmentVariable("PUBSUB_NAME")
      ?? throw new InvalidOperationException("Required environment variable 'PUBSUB_NAME' is not set");

  private readonly CancellationTokenSource _cts = new();
  // Coalescing: one pending wake-up is enough, and two edits in a row must not throw.
  private readonly SemaphoreSlim _changed = new(0, 1);

  public Task StartAsync(CancellationToken cancellationToken)
  {
    var options = new DaprSubscriptionOptions(
      new MessageHandlingPolicy(TimeSpan.FromSeconds(10), TopicResponseAction.Drop));
    _ = StreamingSubscriptions.KeepAsync(pubSubClient, PubSubName, options,
      [new(Topic, HandleAsync)], logger, _cts.Token);
    return Task.CompletedTask;
  }

  private Task<TopicResponseAction> HandleAsync(TopicMessage message, CancellationToken _)
  {
    // The payload carries nothing — the message itself is the signal.
    try
    {
      _changed.Release();
    }
    catch (SemaphoreFullException)
    {
      // The ticker has not consumed the previous wake-up yet, which is exactly what we wanted.
    }
    return Task.FromResult(TopicResponseAction.Success);
  }

  /// Sleeps for at most <paramref name="timeout"/>, returning early when schedules change.
  public async Task WaitForChangeAsync(TimeSpan timeout, CancellationToken ct)
  {
    try
    {
      await _changed.WaitAsync(timeout, ct);
    }
    catch (OperationCanceledException)
    {
      // Shutdown — the caller's loop checks the token.
    }
  }

  public Task StopAsync(CancellationToken _)
  {
    _cts.Cancel();
    return Task.CompletedTask;
  }

  public void Dispose() => _cts.Dispose();
}
