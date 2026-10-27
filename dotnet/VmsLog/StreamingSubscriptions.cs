using Dapr.Messaging.PublishSubscribe;
using Microsoft.Extensions.Logging;

namespace VmsLog;

/// <summary>
/// Keeps one Dapr streaming subscription per topic open for as long as the caller's token lives,
/// opening it again when the sidecar drops it.
/// </summary>
/// <remarks>
/// <see cref="DaprPublishSubscribeClient.SubscribeAsync"/> returns as soon as the call is created,
/// before anything has reached the sidecar: the receiver it hands back runs on, and is the
/// subscription. In Dapr.Messaging 1.18.7 it is an <see cref="IDaprSubscription"/>, whose
/// <c>Completion</c> faults when the stream fails — a restarted sidecar, a broken connection. A
/// sidecar that is not up yet is subtler: subscribing still succeeds, and <c>Completion</c> then
/// completes without an error, the cause swallowed (the test against the real client pins this
/// down). Either is the signal to dispose of the receiver and subscribe again. A subscriber that
/// drops the receiver instead is deaf from its first start, without a word in its log, whenever it
/// starts before its sidecar (compose starts the app first).
/// A stream the sidecar closes cleanly is not reported at all: <c>Completion</c> stays pending, since
/// the receiver's fetch loop ends but the two loops behind it wait on channels only
/// <c>DisposeAsync</c> completes — in 1.18.7, and unchanged in 1.18.10 and 1.19.0 (the test against a
/// sidecar that ends every stream with status OK). Nor is a stream that stays open and delivers
/// nothing. So no receiver is kept longer than
/// <see cref="RetryPolicy.MaxLifetime"/>: then it is disposed of and the topic subscribed again at
/// once. The queue outlives the receiver, so nothing published in between is lost, and a message
/// in flight comes again, which the subscribers' handlers — they only invalidate caches — take as
/// they would any other.
///
/// Each topic is kept separately: one failing does not take the others down with it.
/// </remarks>
public static class StreamingSubscriptions
{
  public sealed record Topic(string Name, TopicMessageHandler Handler);

  /// <summary>How quickly to try again: doubling from <paramref name="Min"/> to <paramref name="Max"/>,
  /// back to <paramref name="Min"/> once a stream has lasted <paramref name="StableAfter"/>; and how
  /// long one receiver is kept at most, <paramref name="MaxLifetime"/>, before it is renewed.</summary>
  public sealed record RetryPolicy(TimeSpan Min, TimeSpan Max, TimeSpan StableAfter, TimeSpan MaxLifetime)
  {
    public static readonly RetryPolicy Default = new(
      TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(15));
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
        // Not "opened": nothing has reached the sidecar yet (see the remarks above).
        logger.LogDebug("Streaming subscription to {Topic} requested", topic.Name);
        var ended = receiver is IDaprSubscription subscription
          ? subscription.Completion
          : Task.Delay(Timeout.Infinite, ct);
        if (!await EndsWithinAsync(ended, retry.MaxLifetime, ct))
        {
          // Lived its full term, so nothing is wrong with it: renewed at once, without a warning.
          logger.LogDebug("Streaming subscription to {Topic} renewed after {Lifetime}", topic.Name, retry.MaxLifetime);
          delay = retry.Min;
          continue;
        }
        reason = "ended without an error (the sidecar not up yet, or it closed the stream)";
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

  // False when the receiver is still open at the end of its lifetime. A receiver that ends on its
  // own — with an error or without — is true, and its error, if any, is the caller's to see.
  private static async Task<bool> EndsWithinAsync(Task completion, TimeSpan lifetime, CancellationToken ct)
  {
    try
    {
      await completion.WaitAsync(lifetime, ct);
      return true;
    }
    catch (TimeoutException) when (!completion.IsCompleted)
    {
      return false;
    }
  }
}
