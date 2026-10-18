using System.Collections.Concurrent;

namespace VmsAnalyticsSingleton;

/// <summary>
/// Short-lived vectors for objects currently visible on live video.
/// </summary>
/// <remarks>
/// Live overlays receive every above-threshold sample, while the database is intentionally
/// debounced. A click on a live box must therefore not depend on a detection row having been
/// committed already. This cache is only a probe source; forensic candidates still come from the
/// persistent embedding table.
/// </remarks>
public sealed class RecentLiveEmbeddingCache
{
  private static readonly TimeSpan MaxProbeAge = TimeSpan.FromMinutes(2);
  private static readonly TimeSpan Retention = TimeSpan.FromMinutes(10);

  private readonly ConcurrentDictionary<(string CameraId, long TrackId, string Kind), Entry> _entries = new();
  private int _offers;

  private sealed record Entry(DateTime DetectedAt, float[] Vector);

  public void Offer(string cameraId, long trackId, string kind, DateTime detectedAt, IReadOnlyList<float> embedding)
  {
    if (!EmbeddingCodec.TryEncodeNormalized(embedding, out var bytes, out var dim))
      return;
    var vector = EmbeddingCodec.DecodeNormalized(bytes, dim);
    if (vector.Length == 0)
      return;

    _entries[(cameraId, trackId, kind)] = new Entry(detectedAt, vector);

    // Opportunistic cleanup keeps this independent of another timer/background service.
    if (Interlocked.Increment(ref _offers) % 256 != 0)
      return;
    var cutoff = DateTime.UtcNow - Retention;
    foreach (var (key, entry) in _entries)
      if (entry.DetectedAt < cutoff)
        _entries.TryRemove(key, out _);
  }

  public bool TryGet(
    string cameraId,
    long trackId,
    string kind,
    DateTime anchor,
    out float[] vector,
    out DateTime detectedAt)
  {
    vector = [];
    detectedAt = default;
    if (!_entries.TryGetValue((cameraId, trackId, kind), out var entry))
      return false;
    if ((entry.DetectedAt - anchor).Duration() > MaxProbeAge)
      return false;

    vector = entry.Vector;
    detectedAt = entry.DetectedAt;
    return true;
  }
}
