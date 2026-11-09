using Dapr.Messaging.PublishSubscribe;
using Grpc.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Vms.Cfg;
using VmsSingleton.EventRouting;

namespace VmsSingleton.Tests;

// A subscription edit must reach the router even when the pub/sub signal is lost. The list is
// therefore checked against the shared version key on every routed event, and not only when the
// one-minute TTL happens to expire — a rule is often exercised by a single event right after it
// was saved, and that event cannot be replayed.
public sealed class SubscriptionCacheTests
{
  // SubscriptionCache reads PUBSUB_NAME in a static initializer the way every subscriber does.
  static SubscriptionCacheTests() =>
    Environment.SetEnvironmentVariable("PUBSUB_NAME", "orderpubsub");

  [Fact]
  public async Task A_moved_version_reloads_the_list_before_the_ttl_expires()
  {
    var cfg = new FakeCfg([Subscription("s1")]);
    var versions = new FakeVersions("v1");
    var cache = NewCache(cfg, versions);

    Assert.Equal(["s1"], (await cache.ListAsync(default)).Select(x => x.Id));

    // Saved behind the cache's back: no pub/sub signal, and the TTL is still fresh.
    cfg.Rows.Add(Subscription("s2"));
    versions.Value = "v2";

    var list = await cache.ListAsync(default);

    Assert.Equal(["s1", "s2"], list.Select(x => x.Id));
    Assert.Equal(2, cfg.Calls);
  }

  [Fact]
  public async Task The_first_version_ever_written_is_seen()
  {
    // A fresh state store has no key at all: the first read answers null, and that must count as a
    // baseline rather than as "never read", or the first real change would be invisible until the
    // TTL — the very failure this cache is being fixed for.
    var cfg = new FakeCfg([Subscription("s1")]);
    var versions = new FakeVersions(null);
    var cache = NewCache(cfg, versions);

    await cache.ListAsync(default);

    cfg.Rows.Add(Subscription("s2"));
    versions.Value = "v1";

    Assert.Equal(2, (await cache.ListAsync(default)).Count);
  }

  [Fact]
  public async Task An_unchanged_version_keeps_the_cached_list()
  {
    var cfg = new FakeCfg([Subscription("s1")]);
    var versions = new FakeVersions("v1");
    var cache = NewCache(cfg, versions);

    await cache.ListAsync(default);
    var list = await cache.ListAsync(default);

    Assert.Single(list);
    Assert.Equal(1, cfg.Calls);
  }

  [Fact]
  public async Task A_version_read_that_fails_keeps_the_cached_list()
  {
    var cfg = new FakeCfg([Subscription("s1")]);
    var versions = new FakeVersions("v1");
    var cache = NewCache(cfg, versions);

    await cache.ListAsync(default);
    versions.Fail = true;

    var list = await cache.ListAsync(default);

    Assert.Single(list);
    Assert.Equal(1, cfg.Calls);
  }

  [Fact]
  public async Task A_change_is_still_seen_after_a_failed_read()
  {
    var cfg = new FakeCfg([Subscription("s1")]);
    var versions = new FakeVersions("v1");
    var cache = NewCache(cfg, versions);
    await cache.ListAsync(default);

    versions.Fail = true;
    await cache.ListAsync(default);

    // The failed read must not swallow the baseline: the next successful one compares against it.
    cfg.Rows.Add(Subscription("s2"));
    versions.Fail = false;
    versions.Value = "v2";

    Assert.Equal(2, (await cache.ListAsync(default)).Count);
  }

  [Fact]
  public async Task The_change_signal_still_forces_a_reload()
  {
    var cfg = new FakeCfg([Subscription("s1")]);
    var versions = new FakeVersions("v1");
    var cache = NewCache(cfg, versions);
    await cache.ListAsync(default);

    // The message arrived but the version key could not be read: Invalidate alone must reload.
    cfg.Rows.Add(Subscription("s2"));
    cache.Invalidate();

    Assert.Equal(2, (await cache.ListAsync(default)).Count);
    Assert.Equal(2, cfg.Calls);
  }

  // ── Fakes ────────────────────────────────────────────────────────────────────────────────────

  private static SubscriptionCache NewCache(FakeCfg cfg, FakeVersions versions) =>
    new(cfg, new FakePubSub(), versions, NullLogger<SubscriptionCache>.Instance);

  private static EventSubscription Subscription(string id) => new()
  {
    Id = id,
    Name = id,
    Enabled = true,
    EventName = "sensor",
    ArmedCondition = "any",
  };

  private sealed class FakeVersions(string? value) : ISubscriptionVersionSource
  {
    public string? Value { get; set; } = value;
    public bool Fail { get; set; }

    public Task<string?> ReadAsync(CancellationToken ct) =>
      Fail
        ? throw new InvalidOperationException("state store is down")
        : Task.FromResult(Value);
  }

  // ListAsync never subscribes; the hosted half of the cache is not exercised here.
  private sealed class FakePubSub() : DaprPublishSubscribeClient(null!, new HttpClient())
  {
    public override Task<IAsyncDisposable> SubscribeAsync(
      string pubSubName, string topicName, DaprSubscriptionOptions options,
      TopicMessageHandler messageHandler, CancellationToken cancellationToken = default) =>
      throw new NotSupportedException("the cache test does not subscribe");
  }

  private sealed class FakeCfg(IEnumerable<EventSubscription> rows) : CfgService.CfgServiceClient
  {
    public List<EventSubscription> Rows { get; } = [.. rows];
    public int Calls { get; private set; }

    public override AsyncUnaryCall<ListEventSubscriptionsResponse> ListEventSubscriptionsAsync(
      ListEventSubscriptionsRequest request, CallOptions options)
    {
      Calls++;
      var response = new ListEventSubscriptionsResponse();
      response.Subscriptions.AddRange(Rows.Select(row => row.Clone()));
      return new AsyncUnaryCall<ListEventSubscriptionsResponse>(
        Task.FromResult(response), Task.FromResult(new Metadata()),
        () => Status.DefaultSuccess, () => [], () => { });
    }
  }
}
