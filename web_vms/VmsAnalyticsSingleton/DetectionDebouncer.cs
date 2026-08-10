using System.Collections.Concurrent;
using Analytics;

namespace VmsAnalyticsSingleton;

// Per-(watch_id, track_id, kind) cooldown — the same physical detection shouldn't retrigger
// RetainEvent/persist every sampled frame while the track stays in view. Keyed per-kind too:
// a single tracked person legitimately produces both a `person` and a `face` event around the
// same instant, and those are distinct detections, not repeats of each other.
public sealed class DetectionDebouncer(TimeSpan window)
{
  // Evict entries idle for this many windows — a track that quiet for that long is gone for
  // good (video_a drops unmatched tracks per frame), so its entry would otherwise leak forever.
  private const int EvictAfterWindows = 10;
  private const int EvictionThreshold = 1024;

  private readonly ConcurrentDictionary<(string WatchId, long TrackId, DetectionKind Kind), DateTime> _lastSeen = new();

  public bool ShouldProcess(string watchId, long trackId, DetectionKind kind, DateTime now)
  {
    if (_lastSeen.Count > EvictionThreshold)
      Evict(now);

    var key = (watchId, trackId, kind);
    if (_lastSeen.TryGetValue(key, out var last) && now - last < window)
      return false;
    _lastSeen[key] = now;
    return true;
  }

  private void Evict(DateTime now)
  {
    foreach (var (key, last) in _lastSeen)
    {
      if (now - last > window * EvictAfterWindows)
        _lastSeen.TryRemove(key, out _);
    }
  }
}
