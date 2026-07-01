using System.Collections.Concurrent;

namespace VmsAnalytics;

// Per-(watch_id, track_id) cooldown — the same physical detection shouldn't retrigger
// RetainEvent/persist every sampled frame while the track stays in view.
public sealed class DetectionDebouncer(TimeSpan window)
{
  private readonly ConcurrentDictionary<(string WatchId, long TrackId), DateTime> _lastSeen = new();

  public bool ShouldProcess(string watchId, long trackId, DateTime now)
  {
    var key = (watchId, trackId);
    if (_lastSeen.TryGetValue(key, out var last) && now - last < window)
      return false;
    _lastSeen[key] = now;
    return true;
  }
}
