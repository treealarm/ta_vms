using Analytics;
using Microsoft.EntityFrameworkCore;

namespace VmsAnalyticsSingleton;

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
  // Must match INCREMENT BY on detections_object_num_seq.
  private const int ObjectNumBlockSize = 100;

  private readonly object _lock = new();
  private readonly LinkedList<Entry> _gallery = new(); // oldest first
  private readonly Dictionary<(string CameraId, long TrackId), TrackLink> _trackToObject = new();

  private readonly IDbContextFactory<AnalyticsDbContext> _dbFactory;
  // Next number to hand out and the first number this process may no longer use. Both guarded by
  // _lock; when they meet, another block is taken from the sequence.
  private long _nextObjectNum;
  private long _blockEnd;

  private sealed record Entry(string ObjectId, float[] Embedding, DateTime SeenAt);
  private sealed record TrackLink(string ObjectId, DateTime SeenAt);

  public EphemeralReidGallery(IDbContextFactory<AnalyticsDbContext> dbFactory)
  {
    _dbFactory = dbFactory;
    _minScore = RequireDouble("ANALYTICS_REID_MATCH_MIN_SCORE");
    _window = TimeSpan.FromSeconds(RequireUint("ANALYTICS_REID_GALLERY_WINDOW_SECONDS"));
  }

  public string Resolve(DetectionKind kind, IReadOnlyList<float> embedding, bool embeddingIsFresh,
    string cameraId, long trackId, string? recognizedText)
  {
    var now = DateTime.UtcNow;
    var trackKey = (cameraId, trackId);

    lock (_lock)
    {
      Evict(now);

      // Only PERSON carries a body embedding today; use it to link across cameras/gaps.
      // The vector rides on every frame of a track, but a gallery sighting is recorded only on a
      // fresh recompute (embeddingIsFresh) — recording every frame would blow the size cap and
      // collapse the time window (see the embedding_is_fresh contract in analytics.proto).
      if (kind == DetectionKind.Person && embedding.Count > 0)
      {
        // Already-identified track: identity is fixed; only re-register a sighting on a fresh frame
        // so the object stays matchable while the track is alive (a long track outlives the window).
        if (_trackToObject.TryGetValue(trackKey, out var known))
        {
          if (embeddingIsFresh)
            _gallery.AddLast(new Entry(known.ObjectId, embedding as float[] ?? embedding.ToArray(), now));
          _trackToObject[trackKey] = known with { SeenAt = now };
          return known.ObjectId;
        }

        // First embedding seen for this track: match it against the gallery to (re-)identify.
        var vec = embedding as float[] ?? embedding.ToArray();
        var matched = BestMatch(vec, now);
        var objectId = matched ?? NewObjectId();
        // Record a sighting on a real observation, or for a brand-new object so it becomes matchable.
        if (embeddingIsFresh || matched is null)
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

  // Object ids are human-readable running numbers ("obj-1", "obj-2", ...) and are persisted on
  // detections and retention_events, so a number must never be handed to two different physical
  // objects. The numbers come from a database sequence that gives out a block at a time
  // (detections_object_num_seq, INCREMENT BY 100): no two processes can be inside the same block,
  // and the database is touched once per hundred objects rather than on the detection path.
  //
  // Callers hold _lock, so the increment is safe.
  private string NewObjectId()
  {
    if (_nextObjectNum >= _blockEnd)
      TakeBlock();
    return $"obj-{_nextObjectNum++}";
  }

  // Caller holds _lock.
  private void TakeBlock()
  {
    using var db = _dbFactory.CreateDbContext();
    var start = db.Database
      .SqlQueryRaw<long>("SELECT nextval('detections_object_num_seq') AS \"Value\"")
      .AsEnumerable()
      .First();
    _nextObjectNum = start;
    _blockEnd = start + ObjectNumBlockSize;
  }

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
