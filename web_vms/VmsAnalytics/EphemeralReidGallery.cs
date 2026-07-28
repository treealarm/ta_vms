using Analytics;
using Microsoft.EntityFrameworkCore;

namespace VmsAnalytics;

// In-memory re-identification: matches an incoming body embedding against recent sightings by
// cosine similarity (dot product — the worker L2-normalizes) and reuses that object's id, else
// mints a new one. Embedding-less frames of a live track resolve via the (camera,track)->object
// cache. Everything lives in memory and is time-windowed; after a restart the gallery is empty and
// objects get fresh ids — acceptable, because historical grouping is already frozen into the
// event_id/event_uuid stored on rows. This is the v1 IObjectIdentityResolver; a persistent
// reference gallery can be layered in front later (see the interface docs).
public sealed class EphemeralReidGallery : IObjectIdentityResolver
{
  private readonly double _minScore;
  private readonly TimeSpan _window;
  private const int MaxEntries = 2000;

  private readonly object _lock = new();
  private readonly LinkedList<Entry> _gallery = new(); // oldest first
  private readonly Dictionary<(string CameraId, long TrackId), TrackLink> _trackToObject = new();
  private long _nextObjectNum; // guarded by _lock

  private sealed record Entry(string ObjectId, float[] Embedding, DateTime SeenAt);
  private sealed record TrackLink(string ObjectId, DateTime SeenAt);

  public EphemeralReidGallery(IDbContextFactory<AnalyticsDbContext> dbFactory)
  {
    _minScore = RequireDouble("ANALYTICS_REID_MATCH_MIN_SCORE");
    _window = TimeSpan.FromSeconds(RequireUint("ANALYTICS_REID_GALLERY_WINDOW_SECONDS"));
    // Object ids are human-readable running numbers ("obj-1", "obj-2", ...). They are persisted on
    // detections/retention_events, so the counter must continue past the highest id ever assigned —
    // a reset to 1 after a restart would reuse a number for a different physical object.
    _nextObjectNum = SeedObjectCounter(dbFactory);
  }

  private static long SeedObjectCounter(IDbContextFactory<AnalyticsDbContext> dbFactory)
  {
    try
    {
      using var db = dbFactory.CreateDbContext();
      return db.Database
        .SqlQueryRaw<long>("SELECT COALESCE(MAX(substring(object_id from 5)::bigint), 0) AS \"Value\" FROM detections WHERE object_id ~ '^obj-[0-9]+$'")
        .AsEnumerable()
        .FirstOrDefault();
    }
    catch
    {
      return 0; // fresh/unavailable DB — start from obj-1
    }
  }

  public string Resolve(DetectionKind kind, IReadOnlyList<float> embedding, string cameraId, long trackId, string? recognizedText)
  {
    var now = DateTime.UtcNow;
    var trackKey = (cameraId, trackId);

    lock (_lock)
    {
      Evict(now);

      // Only PERSON carries a body embedding today; use it to link across cameras/gaps.
      if (kind == DetectionKind.Person && embedding.Count > 0)
      {
        var vec = embedding as float[] ?? embedding.ToArray();
        var objectId = BestMatch(vec, now) ?? NewObjectId();
        _gallery.AddLast(new Entry(objectId, vec, now));
        _trackToObject[trackKey] = new TrackLink(objectId, now);
        return objectId;
      }

      // No embedding: keep the track's already-resolved identity if we have one, else start a fresh
      // per-track identity (this is all vehicles/plates get in v1 — per-track, no cross-camera link).
      if (_trackToObject.TryGetValue(trackKey, out var link))
      {
        _trackToObject[trackKey] = link with { SeenAt = now };
        return link.ObjectId;
      }

      var fresh = NewObjectId();
      _trackToObject[trackKey] = new TrackLink(fresh, now);
      return fresh;
    }
  }

  public string? Peek(string cameraId, long trackId)
  {
    lock (_lock)
      return _trackToObject.TryGetValue((cameraId, trackId), out var link) ? link.ObjectId : null;
  }

  // Caller holds _lock.
  private string? BestMatch(float[] vec, DateTime now)
  {
    var cutoff = now - _window;
    string? best = null;
    double bestScore = _minScore;
    foreach (var e in _gallery)
    {
      if (e.SeenAt < cutoff || e.Embedding.Length != vec.Length)
        continue;
      double dot = 0;
      for (var i = 0; i < vec.Length; i++)
        dot += (double)vec[i] * e.Embedding[i];
      if (dot >= bestScore)
      {
        bestScore = dot;
        best = e.ObjectId;
      }
    }
    return best;
  }

  // Caller holds _lock.
  private void Evict(DateTime now)
  {
    var cutoff = now - _window;
    while (_gallery.First is { } head && (head.Value.SeenAt < cutoff || _gallery.Count > MaxEntries))
      _gallery.RemoveFirst();

    if (_trackToObject.Count > MaxEntries)
    {
      var stale = _trackToObject.Where(kv => kv.Value.SeenAt < cutoff).Select(kv => kv.Key).ToList();
      foreach (var key in stale)
        _trackToObject.Remove(key);
    }
  }

  // Callers hold _lock, so the increment is safe.
  private string NewObjectId() => $"obj-{++_nextObjectNum}";

  private static uint RequireUint(string name)
  {
    var raw = Environment.GetEnvironmentVariable(name)
      ?? throw new InvalidOperationException($"Required environment variable '{name}' is not set");
    return uint.TryParse(raw, out var v)
      ? v
      : throw new InvalidOperationException($"Required environment variable '{name}' must be a non-negative integer");
  }

  private static double RequireDouble(string name)
  {
    var raw = Environment.GetEnvironmentVariable(name)
      ?? throw new InvalidOperationException($"Required environment variable '{name}' is not set");
    return double.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture, out var v)
      ? v
      : throw new InvalidOperationException($"Required environment variable '{name}' must be a number");
  }
}
