using System.Collections.Concurrent;
using System.Net;
using Dapr.Messaging.PublishSubscribe;
using Google.Protobuf;
using Grpc.AspNetCore.Server.Model;
using Grpc.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using VmsLog;
using P = Dapr.Client.Autogen.Grpc.v1;

namespace VmsAnalyticsSingleton.Tests;

// StreamingSubscriptions against a client whose receivers the test ends at will: what Dapr.Messaging
// 1.18.7 does when the sidecar drops a stream (Completion faults) is the case that matters.
public sealed class StreamingSubscriptionsTests
{
  private static readonly DaprSubscriptionOptions Options =
    new(new MessageHandlingPolicy(TimeSpan.FromSeconds(1), TopicResponseAction.Retry));

  private static readonly StreamingSubscriptions.RetryPolicy Fast =
    new(TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(40), TimeSpan.FromHours(1), TimeSpan.FromHours(1));

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

  // What subscribing before the sidecar is up looks like in 1.18.7: SubscribeAsync succeeds and the
  // receiver's Completion faults at once. Until the helper existed, that was a subscriber deaf from
  // its first start.
  [Fact]
  public async Task Subscribing_before_the_sidecar_is_up_keeps_trying_until_a_stream_holds()
  {
    var client = new FakeClient { FailingOpens = 3 };
    var log = new ListLogger();
    using var cts = new CancellationTokenSource();
    var run = StreamingSubscriptions.KeepAsync(client, "pubsub", Options, [new("a", Handler)], log, cts.Token, Fast);

    for (var i = 0; i < 3; i++)
      Assert.True((await client.NextAsync("a")).Completion.IsFaulted);
    var holding = await client.NextAsync("a");
    await Task.Delay(100);

    Assert.False(holding.Completion.IsCompleted);
    Assert.Equal(4, client.OpenedCount("a"));
    Assert.Equal(3, log.Delays.Count);
    cts.Cancel();
    await run.WaitAsync(TimeSpan.FromSeconds(5));
  }

  [Fact]
  public async Task A_stream_that_ends_without_an_error_is_opened_again()
  {
    var client = new FakeClient();
    using var cts = new CancellationTokenSource();
    var run = StreamingSubscriptions.KeepAsync(client, "pubsub", Options, [new("a", Handler)],
      new ListLogger(), cts.Token, Fast);

    var first = await client.NextAsync("a");
    first.End();
    await client.NextAsync("a");

    Assert.Equal(1, first.Disposed);
    cts.Cancel();
    await run.WaitAsync(TimeSpan.FromSeconds(5));
  }

  // The real 1.18.7 client against a port nobody listens on, i.e. a sidecar not started yet:
  // SubscribeAsync succeeds and Completion completes without an error, the cause lost. Whatever a
  // later Dapr makes of that, the helper must still come back for another try.
  [Fact]
  public async Task The_real_client_without_a_sidecar_is_retried()
  {
    var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
    listener.Start();
    var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
    listener.Stop();

    var client = new DaprPublishSubscribeClientBuilder().UseGrpcEndpoint($"http://127.0.0.1:{port}").Build();
    var log = new ListLogger();
    using var cts = new CancellationTokenSource();
    var run = StreamingSubscriptions.KeepAsync(client, "pubsub", Options, [new("a", Handler)], log, cts.Token, Fast);

    await WaitUntilAsync(() => log.Delays.Count >= 2);
    cts.Cancel();
    await run.WaitAsync(TimeSpan.FromSeconds(5));
  }

  [Fact]
  public async Task A_stream_that_outlives_its_lifetime_is_renewed_at_once()
  {
    var client = new FakeClient();
    var log = new ListLogger();
    using var cts = new CancellationTokenSource();
    var run = StreamingSubscriptions.KeepAsync(client, "pubsub", Options, [new("a", Handler)], log, cts.Token,
      Fast with { MaxLifetime = TimeSpan.FromMilliseconds(100) });

    var first = await client.NextAsync("a");
    await client.NextAsync("a");

    Assert.Equal(1, first.Disposed);
    Assert.False(first.Completion.IsCompleted);
    // Renewed, not retried: no wait and no warning.
    Assert.Empty(log.Delays);
    cts.Cancel();
    await run.WaitAsync(TimeSpan.FromSeconds(5));
  }

  // The real client against a sidecar that ends every stream with status OK. Completion never
  // completes then (still so in Dapr.Messaging 1.19), so only the lifetime brings the subscription
  // back; should a later Dapr report the clean end, this still passes, through the retry instead.
  [Fact]
  public async Task The_real_client_is_subscribed_again_when_the_sidecar_ends_the_stream_cleanly()
  {
    await using var sidecar = await CleanEndingSidecar.StartAsync();
    var client = new DaprPublishSubscribeClientBuilder().UseGrpcEndpoint(sidecar.Url).Build();
    using var cts = new CancellationTokenSource();
    var run = StreamingSubscriptions.KeepAsync(client, "pubsub", Options, [new("a", Handler)], new ListLogger(),
      cts.Token, Fast with { MaxLifetime = TimeSpan.FromMilliseconds(300) });

    await WaitUntilAsync(() => sidecar.Subscriptions >= 2);
    cts.Cancel();
    await run.WaitAsync(TimeSpan.FromSeconds(5));
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
    public void End() => _done.TrySetResult();
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
    // How many receivers, per topic, fail as soon as they are handed out.
    public int FailingOpens { get; init; }

    public int OpenedCount(string topic) => _opened.GetValueOrDefault(topic);

    public override Task<IAsyncDisposable> SubscribeAsync(string pubSubName, string topicName,
      DaprSubscriptionOptions options, TopicMessageHandler messageHandler, CancellationToken cancellationToken = default)
    {
      if (SubscribeError is not null) throw SubscribeError;
      var receiver = new FakeReceiver();
      if (_opened.AddOrUpdate(topicName, 1, (_, n) => n + 1) <= FailingOpens)
        receiver.Fail();
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

  // Answers SubscribeTopicEventsAlpha1 as a sidecar does, then returns: the call ends with status
  // OK, which is a clean end of the response stream.
  private sealed class CleanEndingSidecar : IAsyncDisposable
  {
    private readonly WebApplication _app;
    private CleanEndingSidecar(WebApplication app) => _app = app;

    public string Url => _app.Urls.First();
    public int Subscriptions => _app.Services.GetRequiredService<Counter>().Value;

    public static async Task<CleanEndingSidecar> StartAsync()
    {
      var builder = WebApplication.CreateBuilder();
      builder.Logging.ClearProviders();
      builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0, o => o.Protocols = HttpProtocols.Http2));
      builder.Services.AddGrpc();
      builder.Services.AddSingleton<Counter>();
      builder.Services.AddSingleton<IServiceMethodProvider<Service>, Service.Provider>();
      var app = builder.Build();
      app.MapGrpcService<Service>();
      await app.StartAsync();
      return new CleanEndingSidecar(app);
    }

    public async ValueTask DisposeAsync()
    {
      await _app.StopAsync();
      await _app.DisposeAsync();
    }

    private sealed class Counter
    {
      private int _value;
      public int Value => Volatile.Read(ref _value);
      public void Increment() => Interlocked.Increment(ref _value);
    }

    private sealed class Service(Counter subscriptions)
    {
      private static Marshaller<T> Of<T>(MessageParser<T> parser) where T : IMessage<T> =>
        Marshallers.Create(m => m.ToByteArray(), parser.ParseFrom);

      private static readonly Method<P.SubscribeTopicEventsRequestAlpha1, P.SubscribeTopicEventsResponseAlpha1> Subscribe =
        new(MethodType.DuplexStreaming, "dapr.proto.runtime.v1.Dapr", "SubscribeTopicEventsAlpha1",
          Of(P.SubscribeTopicEventsRequestAlpha1.Parser), Of(P.SubscribeTopicEventsResponseAlpha1.Parser));

      private async Task SubscribeAsync(IAsyncStreamReader<P.SubscribeTopicEventsRequestAlpha1> requests,
        IServerStreamWriter<P.SubscribeTopicEventsResponseAlpha1> responses, ServerCallContext call)
      {
        if (!await requests.MoveNext(call.CancellationToken)) return;
        subscriptions.Increment();
        await responses.WriteAsync(new P.SubscribeTopicEventsResponseAlpha1 { InitialResponse = new() });
      }

      public sealed class Provider : IServiceMethodProvider<Service>
      {
        public void OnServiceMethodDiscovery(ServiceMethodProviderContext<Service> context) =>
          context.AddDuplexStreamingMethod(Subscribe, [],
            (service, requests, responses, call) => service.SubscribeAsync(requests, responses, call));
      }
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
