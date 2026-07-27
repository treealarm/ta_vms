namespace VmsAnalytics;

// Chooses the event_id (group label) for a detection given its object_id. A reappearance of the
// same object within ANALYTICS_EVENT_GROUP_GAP_SECONDS reuses the same event_id (its episodes group
// together in the UI tree); a longer gap starts a new event_id. VmsFs then decides, per event_id,
// whether to extend the open episode or open a new event_uuid — this service owns only the grouping
// window, not the episode lifecycle. In-memory and evicted; after a restart every object starts a
// new group, which is acceptable since historical grouping is frozen into stored event_id values.
public sealed class ObjectSessionService
{
  // Hard cap on how much continuous detection maps to a single event_id. A continuously-present (or
  // continuously re-detected) object would otherwise pile every clip into one ever-growing event.
  // Past this, a fresh event_id is minted, so a long presence becomes a series of separate, bounded
  // events in the list rather than one unbounded group.
  private static readonly TimeSpan MaxEventDuration = TimeSpan.FromMinutes(3);

  private readonly TimeSpan _gap;
  private const int EvictThreshold = 4096;

  private readonly object _lock = new();
  private readonly Dictionary<string, Session> _sessions = new();

  private sealed record Session(string EventId, DateTime LastSeen, DateTime StartedAt);

  public ObjectSessionService()
  {
    _gap = TimeSpan.FromSeconds(RequireUint("ANALYTICS_EVENT_GROUP_GAP_SECONDS"));
  }

  public string SelectEventId(string objectId, DateTime detectedAt)
  {
    lock (_lock)
    {
      if (_sessions.Count > EvictThreshold)
        Evict(detectedAt);

      // Reuse the event_id only for a genuine near-continuous session: same object, within the gap,
      // and under the max duration. A longer gap (reappearance) OR hitting the duration cap starts a
      // new event_id.
      if (_sessions.TryGetValue(objectId, out var s)
          && detectedAt - s.LastSeen < _gap
          && detectedAt - s.StartedAt < MaxEventDuration)
      {
        _sessions[objectId] = s with { LastSeen = detectedAt };
        return s.EventId;
      }

      var eventId = $"evt-{Guid.NewGuid()}";
      _sessions[objectId] = new Session(eventId, detectedAt, detectedAt);
      return eventId;
    }
  }

  private void Evict(DateTime now)
  {
    var cutoff = now - _gap;
    foreach (var key in _sessions.Where(kv => kv.Value.LastSeen < cutoff).Select(kv => kv.Key).ToList())
      _sessions.Remove(key);
  }

  private static uint RequireUint(string name)
  {
    var raw = Environment.GetEnvironmentVariable(name)
      ?? throw new InvalidOperationException($"Required environment variable '{name}' is not set");
    return uint.TryParse(raw, out var v)
      ? v
      : throw new InvalidOperationException($"Required environment variable '{name}' must be a non-negative integer");
  }
}
