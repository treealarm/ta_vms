using System.Collections.Concurrent;
using Dapr.Messaging.PublishSubscribe;
using Microsoft.Extensions.Logging;
using VmsLog;

namespace VmsAnalyticsSingleton.Tests;

// StreamingSubscriptions against a client whose receivers the test ends at will: what Dapr.Messaging
// 1.18.7 does when the sidecar drops a stream (Completion faults) is the case that matters.
public sealed class StreamingSubscriptionsTests
{
  private static readonly DaprSubscriptionOptions Options =
    new(new MessageHandlingPolicy(TimeSpan.FromSeconds(1), TopicResponseAction.Retry));

  private static readonly StreamingSubscriptions.RetryPolicy Fast =
    new(TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(40), TimeSpan.FromHours(1));

  private static Task<TopicResponseAction> Handler(TopicMessage _, CancellationToken __) =>
    Task.FromResult(TopicResponseAction.Success);

  [Fact]
  public async Task A_failed_stream_is_disposed_of_and_opened_again()
  {
    var client = new FakeClient();
    using var cts = new CancellationTokenSource();
    var run = StreamingSubscriptions.KeepAsync(client, "pubsub", Options, [new("a", Handler)],
      new ListLogger(), cts.Token, Fast);

    var first = await client.NextAsync("a");
    first.Fail();
    var second = await client.NextAsync("a");

    Assert.Equal(1, first.Disposed);
    Assert.Equal(0, second.Disposed);
    cts.Cancel();
    await run.WaitAsync(TimeSpan.FromSeconds(5));
    Assert.Equal(1, second.Disposed);
  }

  [Fact]
  public async Task One_topic_failing_leaves_the_others_alone()
  {
    var client = new FakeClient();
    using var cts = new CancellationTokenSource();
    var run = StreamingSubscriptions.KeepAsync(client, "pubsub", Options,
      [new("a", Handler), new("b", Handler)], new ListLogger(), cts.Token, Fast);

    var a = await client.NextAsync("a");
    var b = await client.NextAsync("b");
    a.Fail();
    await client.NextAsync("a");

    Assert.Equal(1, client.OpenedCount("b"));
    Assert.Equal(0, b.Disposed);
    cts.Cancel();
    await run.WaitAsync(TimeSpan.FromSeconds(5));
  }

  [Fact]
  public async Task Stopping_disposes_of_every_receiver()
  {
    var client = new FakeClient();
    using var cts = new CancellationTokenSource();
    var run = StreamingSubscriptions.KeepAsync(client, "pubsub", Options,
      [new("a", Handler), new("b", Handler), new("c", Handler)], new ListLogger(), cts.Token, Fast);
    var receivers = new[] { await client.NextAsync("a"), await client.NextAsync("b"), await client.NextAsync("c") };

    cts.Cancel();
    await run.WaitAsync(TimeSpan.FromSeconds(5));

    Assert.All(receivers, r => Assert.Equal(1, r.Disposed));
  }

  [Fact]
  public async Task The_wait_doubles_up_to_the_ceiling_while_subscribing_keeps_failing()
  {
    var client = new FakeClient { SubscribeError = new InvalidOperationException("sidecar not up") };
    var log = new ListLogger();
    using var cts = new CancellationTokenSource();
    var run = StreamingSubscriptions.KeepAsync(client, "pubsub", Options, [new("a", Handler)], log, cts.Token, Fast);

    await WaitUntilAsync(() => log.Delays.Count >= 5);
    cts.Cancel();
    await run.WaitAsync(TimeSpan.FromSeconds(5));

    Assert.Equal([0.01, 0.02, 0.04, 0.04, 0.04], log.Delays.Take(5).ToArray());
    Assert.Contains(log.Messages, m => m.Contains("sidecar not up"));
  }

  [Fact]
  public async Task A_stream_that_lasted_resets_the_wait()
  {
    var client = new FakeClient();
    var log = new ListLogger();
    using var cts = new CancellationTokenSource();
    var lasting = Fast with { StableAfter = TimeSpan.FromMilliseconds(200) };
    var run = StreamingSubscriptions.KeepAsync(client, "pubsub", Options, [new("a", Handler)], log, cts.Token, lasting);

    (await client.NextAsync("a")).Fail();
    (await client.NextAsync("a")).Fail();
    var third = await client.NextAsync("a");
    await Task.Delay(300);
    third.Fail();
    await client.NextAsync("a");
    cts.Cancel();
    await run.WaitAsync(TimeSpan.FromSeconds(5));

    Assert.Equal([0.01, 0.02, 0.01], log.Delays.Take(3).ToArray());
  }

  private static async Task WaitUntilAsync(Func<bool> condition)
  {
    var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
    while (!condition())
    {
      if (DateTime.UtcNow > deadline) throw new TimeoutException();
      await Task.Delay(5);
    }
  }

  private sealed class FakeReceiver : IDaprSubscription
  {
    private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _disposed;
    public int Disposed => _disposed;
    public Task Completion => _done.Task;
    public void Fail() => _done.TrySetException(new InvalidOperationException("stream dropped"));
    public ValueTask DisposeAsync()
    {
      Interlocked.Increment(ref _disposed);
      return ValueTask.CompletedTask;
    }
  }

  private sealed class FakeClient() : DaprPublishSubscribeClient(null!, new HttpClient())
  {
    private readonly ConcurrentDictionary<string, ConcurrentQueue<FakeReceiver>> _pending = new();
    private readonly ConcurrentDictionary<string, int> _opened = new();
    public Exception? SubscribeError { get; init; }

    public int OpenedCount(string topic) => _opened.GetValueOrDefault(topic);

    public override Task<IAsyncDisposable> SubscribeAsync(string pubSubName, string topicName,
      DaprSubscriptionOptions options, TopicMessageHandler messageHandler, CancellationToken cancellationToken = default)
    {
      if (SubscribeError is not null) throw SubscribeError;
      var receiver = new FakeReceiver();
      _opened.AddOrUpdate(topicName, 1, (_, n) => n + 1);
      _pending.GetOrAdd(topicName, _ => new()).Enqueue(receiver);
      return Task.FromResult<IAsyncDisposable>(receiver);
    }

    // The next receiver opened for the topic, in order.
    public async Task<FakeReceiver> NextAsync(string topic)
    {
      var queue = _pending.GetOrAdd(topic, _ => new());
      FakeReceiver? receiver = null;
      await WaitUntilAsync(() => queue.TryDequeue(out receiver));
      return receiver!;
    }
  }

  private sealed class ListLogger : ILogger
  {
    private readonly ConcurrentQueue<string> _messages = new();
    private readonly ConcurrentQueue<double> _delays = new();
    public IReadOnlyCollection<string> Messages => _messages;
    public IReadOnlyCollection<double> Delays => _delays;

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
      Func<TState, Exception?, string> formatter)
    {
      _messages.Enqueue(formatter(state, exception));
      if (state is IReadOnlyList<KeyValuePair<string, object?>> fields)
      {
        foreach (var (key, value) in fields)
          if (key == "Delay" && value is double d) _delays.Enqueue(Math.Round(d, 3));
      }
    }
  }
}
