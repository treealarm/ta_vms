using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dapr.Client;
using Vms.Cfg;

namespace VmsCfg;

/// <summary>
/// Last-known observed parameters of each stream, as video_srv measured them.
///
/// Written by every video_srv into the shared state store under `stream-stats-{camera}/{stream}`
/// with a TTL, read here so the camera catalogue can hand them to the monitor. Two consequences of
/// that shape are load-bearing:
///
/// Staleness needs no age check anywhere — an entry that stops being refreshed expires and simply
/// stops coming back, so "no answer" and "too old" are the same case.
///
/// One bulk read per catalogue listing, not one read per stream. ListMediaServers reads its
/// heartbeats key-by-key in a loop, which is fine for a handful of nodes and would not be for
/// cameras times streams.
///
/// The short cache on top exists because a wall of tiles polls the catalogue continuously while
/// these values change on the order of seconds.
/// </summary>
public sealed class StreamStatsCache(DaprClient dapr, ILogger<StreamStatsCache> logger)
{
  private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(2);

  /// <summary>
  /// Hard ceiling on what these values may cost the catalogue. They are an enrichment — the camera
  /// list is correct and complete without them — so a state store that is slow, or a Dapr sidecar
  /// that is not running at all, must cost a bounded wait and nothing else. Without this the
  /// catalogue inherits the store's latency, and web_vms.Server's own five-second deadline turns
  /// that into "vmscfg is unavailable" with no cameras on screen.
  /// </summary>
  private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(2);

  private static readonly string StateStoreName =
    Environment.GetEnvironmentVariable("DAPR_STATE_STORE_NAME")
      ?? throw new InvalidOperationException("Required environment variable 'DAPR_STATE_STORE_NAME' is not set");

  private static readonly JsonSerializerOptions JsonOptions =
    new(JsonSerializerDefaults.Web);

  private sealed record Snapshot
  {
    [JsonPropertyName("width")] public int Width { get; init; }
    [JsonPropertyName("height")] public int Height { get; init; }
    [JsonPropertyName("codec")] public string Codec { get; init; } = string.Empty;
    [JsonPropertyName("fps")] public double Fps { get; init; }
    [JsonPropertyName("bitrateBps")] public long BitrateBps { get; init; }
    [JsonPropertyName("observedAtMs")] public long ObservedAtMs { get; init; }
    [JsonPropertyName("transcodeEncoder")] public string TranscodeEncoder { get; init; } = string.Empty;
  }

  // Freshness is tracked per key rather than for the cache as a whole, because the two callers ask
  // for very different key sets: the catalogue listing asks for every stream at once, while each
  // tile polls one camera's state every few seconds. One shared "loaded at" would let a one-camera
  // request be answered out of a listing's cache without its own entry, or force a bulk read per
  // poll. A key with no snapshot is remembered as such (null), so streams nobody measures do not
  // cost a round trip every time either.
  //
  // The dictionary is concurrent so the common case — everything still fresh — reads without taking
  // the lock at all; the lock guards only a refresh, which is a network call and must not be
  // entered again by every request arriving while one is in flight.
  private readonly SemaphoreSlim _refreshLock = new(1, 1);
  private readonly ConcurrentDictionary<string, (Snapshot? Value, DateTime LoadedAtUtc)> _cache = new();

  private static string KeyOf(string cameraId, string streamName) =>
    $"stream-stats-{cameraId}/{streamName}";

  /// <summary>
  /// Fills in <c>stats</c> on every stream that has a fresh snapshot. Streams without one are left
  /// untouched: absent means "not measured", which the monitor answers by falling back to the
  /// declared resolution and then to its previous behaviour.
  /// </summary>
  public async Task ApplyAsync(IReadOnlyList<CameraSetup> cameras, CancellationToken ct)
  {
    var keys = cameras
      .SelectMany(c => c.Streams.Select(s => KeyOf(c.CameraId, s.StreamName)))
      .Distinct()
      .ToList();

    if (keys.Count == 0)
      return;

    var snapshots = await GetAsync(keys, ct);

    foreach (var camera in cameras)
    {
      foreach (var stream in camera.Streams)
      {
        if (!snapshots.TryGetValue(KeyOf(camera.CameraId, stream.StreamName), out var snap) || snap is null)
          continue;

        stream.Stats = new StreamStats
        {
          Width = snap.Width,
          Height = snap.Height,
          Codec = snap.Codec,
          Fps = snap.Fps,
          BitrateBps = snap.BitrateBps,
          ObservedAtMs = snap.ObservedAtMs,
          TranscodeEncoder = snap.TranscodeEncoder,
        };
      }
    }
  }

  private async Task<Dictionary<string, Snapshot?>> GetAsync(
    IReadOnlyList<string> keys, CancellationToken ct)
  {
    if (StaleKeys(keys).Count > 0)
    {
      await _refreshLock.WaitAsync(ct);
      try
      {
        // Re-checked under the lock: whoever held it may already have fetched exactly these.
        var stale = StaleKeys(keys);
        if (stale.Count > 0)
          await RefreshAsync(stale, DateTime.UtcNow, ct);
      }
      finally
      {
        _refreshLock.Release();
      }
    }

    var result = new Dictionary<string, Snapshot?>(keys.Count);
    foreach (var key in keys)
      if (_cache.TryGetValue(key, out var entry))
        result[key] = entry.Value;
    return result;
  }

  private List<string> StaleKeys(IReadOnlyList<string> keys)
  {
    var now = DateTime.UtcNow;
    return keys
      .Where(k => !_cache.TryGetValue(k, out var entry) || now - entry.LoadedAtUtc >= CacheTtl)
      .ToList();
  }

  private async Task RefreshAsync(IReadOnlyList<string> keys, DateTime now, CancellationToken ct)
  {
    IReadOnlyList<BulkStateItem> items;
    try
    {
      using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
      deadline.CancelAfter(ReadTimeout);
      items = await dapr.GetBulkStateAsync(
        StateStoreName, keys, parallelism: null, cancellationToken: deadline.Token);
    }
    catch (Exception ex) when (!ct.IsCancellationRequested)
    {
      // The catalogue is useful without these — the monitor falls back to declared resolutions.
      //
      // The keys are marked as checked even though nothing was read, so a store that is down costs
      // one attempt per TTL rather than one per request. Existing values are left in place: a
      // single failed read is no reason to forget what was measured a moment ago.
      logger.LogWarning(ex, "Reading stream statistics from the state store failed");
      foreach (var key in keys)
        _cache[key] = (_cache.TryGetValue(key, out var previous) ? previous.Value : null, now);
      return;
    }

    foreach (var item in items)
    {
      Snapshot? value = null;
      if (!string.IsNullOrEmpty(item.Value))
      {
        try
        {
          value = JsonSerializer.Deserialize<Snapshot>(item.Value, JsonOptions);
        }
        catch (JsonException ex)
        {
          // One malformed entry must not cost the whole listing its statistics.
          logger.LogWarning(ex, "Unreadable stream statistics under key '{Key}'", item.Key);
        }
      }

      _cache[item.Key] = (value, now);
    }
  }
}
