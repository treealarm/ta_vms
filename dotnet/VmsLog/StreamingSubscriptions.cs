using Dapr.Messaging.PublishSubscribe;
using Microsoft.Extensions.Logging;

namespace VmsLog;

/// <summary>
/// Keeps one Dapr streaming subscription per topic open for as long as the caller's token lives,
/// opening it again when the sidecar drops it.
/// </summary>
/// <remarks>
/// <see cref="DaprPublishSubscribeClient.SubscribeAsync"/> returns as soon as the stream is opened:
/// the receiver it hands back runs on, and is the subscription. In Dapr.Messaging 1.18.7 it is an
/// <see cref="IDaprSubscription"/>, whose <c>Completion</c> faults when the stream fails — a
/// restarted sidecar, a broken connection. That is the signal to dispose of it and subscribe again.
/// A stream the sidecar closes cleanly is not reported by that version (<c>Completion</c> stays
/// pending), so a subscriber that must not go stale still needs a periodic resync of its own.
///
/// Each topic is kept separately: one failing does not take the others down with it.
/// </remarks>
public static class StreamingSubscriptions
{
  public sealed record Topic(string Name, TopicMessageHandler Handler);

  /// <summary>How quickly to try again: doubling from <paramref name="Min"/> to <paramref name="Max"/>,
  /// back to <paramref name="Min"/> once a stream has lasted <paramref name="StableAfter"/>.</summary>
  public sealed record RetryPolicy(TimeSpan Min, TimeSpan Max, TimeSpan StableAfter)
  {
    public static readonly RetryPolicy Default =
      new(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1));
  }

  /// <summary>Runs until <paramref name="ct"/> is cancelled; every receiver is disposed of on the way out.</summary>
  public static Task KeepAsync(
    DaprPublishSubscribeClient client,
    string pubSubName,
    DaprSubscriptionOptions options,
    IReadOnlyList<Topic> topics,
    ILogger logger,
    CancellationToken ct,
    RetryPolicy? retry = null) =>
    Task.WhenAll(topics.Select(t => KeepOneAsync(client, pubSubName, options, t, logger, retry ?? RetryPolicy.Default, ct)));

  private static async Task KeepOneAsync(
    DaprPublishSubscribeClient client,
    string pubSubName,
    DaprSubscriptionOptions options,
    Topic topic,
    ILogger logger,
    RetryPolicy retry,
    CancellationToken ct)
  {
    var delay = retry.Min;
    while (!ct.IsCancellationRequested)
    {
      var opened = DateTime.UtcNow;
      IAsyncDisposable? receiver = null;
      string reason;
      try
      {
        receiver = await client.SubscribeAsync(pubSubName, topic.Name, options, topic.Handler, ct);
        logger.LogInformation("Streaming subscription to {Topic} opened", topic.Name);
        if (receiver is IDaprSubscription subscription)
          await subscription.Completion.WaitAsync(ct);
        else
          await Task.Delay(Timeout.Infinite, ct);
        reason = "ended";
      }
      catch (OperationCanceledException) when (ct.IsCancellationRequested)
      {
        return;
      }
      catch (Exception ex)
      {
        reason = $"failed ({ex.GetBaseException().Message})";
      }
      finally
      {
        if (receiver is not null)
          await receiver.DisposeAsync();
      }

      if (DateTime.UtcNow - opened >= retry.StableAfter)
        delay = retry.Min;
      logger.LogWarning("Streaming subscription to {Topic} {Reason}; subscribing again in {Delay:0.###}s",
        topic.Name, reason, delay.TotalSeconds);
      try
      {
        await Task.Delay(delay, ct);
      }
      catch (OperationCanceledException)
      {
        return;
      }
      delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, retry.Max.Ticks));
    }
  }
}
