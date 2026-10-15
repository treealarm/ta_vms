using System.Collections.Concurrent;

namespace VmsPtzTrackingSingleton;

/// <summary>What a <see cref="TtlCache{T}"/> could say about a key.</summary>
/// <param name="Value">The configuration, null when VmsCfg says there is none.</param>
/// <param name="Failed">
/// VmsCfg could not be read. <see cref="Value"/> is then the last answer held, or null when there
/// never was one -- which must not be taken for "there is none".
/// </param>
public readonly record struct CachedLookup<T>(T? Value, bool Failed) where T : class;

/// <summary>
/// Per-camera configuration read from VmsCfg, fitted once per change rather than per detection.
/// </summary>
/// <remarks>
/// <para>
/// Every cache in this service behaves alike, and has to: invalidated through
/// <c>server-config-changed</c> by camera id (empty meaning all), with a TTL as the backstop for a
/// lost event, and a failed read is never an answer -- the camera keeps aiming by what it had
/// through a VmsCfg hiccup instead of going blind.
/// </para>
/// <para>
/// Invalidating therefore marks an entry stale rather than dropping it. Dropped, the next read
/// failing would leave nothing to fall back on, and a camera whose calibration was merely
/// unreadable would be stopped as though it had none.
/// </para>
/// </remarks>
/// <param name="load">Reads one camera's configuration; null when VmsCfg has none.</param>
/// <param name="what">What is cached, for the log.</param>
public sealed class TtlCache<T>(
  Func<string, CancellationToken, Task<T?>> load,
  TimeSpan ttl,
  ILogger logger,
  string what) where T : class
{
  private sealed record Entry(T? Value, DateTime FetchedAt);

  private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

  /// <param name="cameraIds">The cameras to re-read; empty re-reads every one.</param>
  public void Invalidate(IReadOnlyCollection<string> cameraIds)
  {
    foreach (var cameraId in cameraIds.Count == 0 ? _entries.Keys.ToList() : cameraIds)
    {
      // Conditional, so an answer fetched meanwhile is not marked stale by an older event.
      if (_entries.TryGetValue(cameraId, out var entry))
        _entries.TryUpdate(cameraId, entry with { FetchedAt = DateTime.MinValue }, entry);
    }
  }

  /// <param name="fresh">Read past the TTL, for a caller acting on something just changed.</param>
  public async Task<CachedLookup<T>> LookupAsync(string cameraId, bool fresh, CancellationToken ct)
  {
    _entries.TryGetValue(cameraId, out var cached);
    if (!fresh && cached is not null && DateTime.UtcNow - cached.FetchedAt < ttl)
      return new CachedLookup<T>(cached.Value, false);

    try
    {
      var value = await load(cameraId, ct);
      _entries[cameraId] = new Entry(value, DateTime.UtcNow);
      return new CachedLookup<T>(value, false);
    }
    catch (Exception ex) when (!ct.IsCancellationRequested)
    {
      logger.LogWarning(ex, "Could not load the {What} of camera '{Camera}'", what, cameraId);
      return new CachedLookup<T>(cached?.Value, true);
    }
  }

  public async Task<T?> GetAsync(string cameraId, CancellationToken ct) =>
    (await LookupAsync(cameraId, fresh: false, ct)).Value;

  /// <summary>Re-reads one camera, ignoring the TTL; on failure, what was held.</summary>
  public async Task<T?> ReloadAsync(string cameraId, CancellationToken ct) =>
    (await LookupAsync(cameraId, fresh: true, ct)).Value;
}
