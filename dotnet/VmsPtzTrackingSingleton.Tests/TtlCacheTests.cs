using Microsoft.Extensions.Logging.Abstractions;

namespace VmsPtzTrackingSingleton.Tests;

public class TtlCacheTests
{
  private sealed class Source
  {
    public string? Answer { get; set; } = "v1";
    public bool Down { get; set; }
    public int Reads { get; private set; }

    public Task<string?> LoadAsync(string key, CancellationToken ct)
    {
      Reads++;
      return Down ? throw new InvalidOperationException("VmsCfg is down") : Task.FromResult(Answer);
    }
  }

  private static TtlCache<string> Cache(Source source) =>
    new(source.LoadAsync, TimeSpan.FromMinutes(1), NullLogger.Instance, "test value");

  [Fact]
  public async Task ReadsOncePerTtl()
  {
    var source = new Source();
    var cache = Cache(source);

    Assert.Equal("v1", await cache.GetAsync("cam", default));
    Assert.Equal("v1", await cache.GetAsync("cam", default));
    Assert.Equal(1, source.Reads);
  }

  [Fact]
  public async Task AnInvalidatedEntryIsReadAgain()
  {
    var source = new Source();
    var cache = Cache(source);
    await cache.GetAsync("cam", default);

    source.Answer = "v2";
    cache.Invalidate([]);

    Assert.Equal("v2", await cache.GetAsync("cam", default));
  }

  [Fact]
  public async Task AFailedReadAfterAnInvalidationKeepsWhatWasKnown()
  {
    var source = new Source();
    var cache = Cache(source);
    await cache.GetAsync("cam", default);

    cache.Invalidate(["cam"]);
    source.Down = true;
    var lookup = await cache.LookupAsync("cam", fresh: true, default);

    Assert.True(lookup.Failed);
    Assert.Equal("v1", lookup.Value);
  }

  [Fact]
  public async Task AFailedFirstReadIsNotTakenForNone()
  {
    var source = new Source { Down = true };
    var cache = Cache(source);

    var failed = await cache.LookupAsync("cam", fresh: false, default);
    source.Down = false;
    source.Answer = null;
    var none = await cache.LookupAsync("cam", fresh: false, default);

    Assert.Equal((null, true), (failed.Value, failed.Failed));
    Assert.Equal((null, false), (none.Value, none.Failed));
  }
}
