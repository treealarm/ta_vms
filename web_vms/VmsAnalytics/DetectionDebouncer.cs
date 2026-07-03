using System.Collections.Concurrent;

namespace VmsAnalytics;

// Per-(watch_id, track_id) cooldown — the same physical detection shouldn't retrigger
// RetainEvent/persist every sampled frame while the track stays in view.
public sealed class DetectionDebouncer(TimeSpan window)
{
  // Evict entries idle for this many windows — a track that quiet for that long is gone for
  // good (video_a drops unmatched tracks per frame), so its entry would otherwise leak forever.
  private const int EvictAfterWindows = 10;
  private const int EvictionThreshold = 1024;

  private readonly ConcurrentDictionary<(string WatchId, long TrackId), DateTime> _lastSeen = new();

  public bool ShouldProcess(string watchId, long trackId, DateTime now)
  {
    if (_lastSeen.Count > EvictionThreshold)
      Evict(now);

    var key = (watchId, trackId);
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
