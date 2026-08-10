using System.Collections.Concurrent;

namespace VmsSingleton;

// In-memory registry of debug "entity state" reports received over the
// debug-entity-state pub/sub topic. Best-effort, non-persistent: entries
// disappear on restart and are removed once a publisher stops pinging.
public sealed class EntityStateRegistry
{
  public static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(30);
  public static readonly TimeSpan RemoveAfter = TimeSpan.FromMinutes(5);

  public sealed record Key(string Service, string EntityType, string EntityId);

  public sealed record Entry(string StateJson, DateTime LastSeenUtc, bool Stale);

  private sealed class MutableEntry
  {
    public required string StateJson;
    public DateTime LastSeenUtc;
  }

  private readonly ConcurrentDictionary<Key, MutableEntry> _entries = new();

  public void Upsert(string service, string entityType, string entityId, string stateJson)
  {
    var key = new Key(service, entityType, entityId);
    _entries.AddOrUpdate(
      key,
      _ => new MutableEntry { StateJson = stateJson, LastSeenUtc = DateTime.UtcNow },
      (_, existing) =>
      {
        existing.StateJson = stateJson;
        existing.LastSeenUtc = DateTime.UtcNow;
        return existing;
      });
  }

  public IReadOnlyList<(Key Key, Entry Entry)> GetAll()
  {
    var now = DateTime.UtcNow;
    return _entries
      .Select(kv => (kv.Key, new Entry(kv.Value.StateJson, kv.Value.LastSeenUtc, now - kv.Value.LastSeenUtc > StaleAfter)))
      .ToList();
  }

  public void Sweep()
  {
    var now = DateTime.UtcNow;
    foreach (var (key, entry) in _entries)
    {
      if (now - entry.LastSeenUtc > RemoveAfter)
        _entries.TryRemove(key, out _);
    }
  }
}
