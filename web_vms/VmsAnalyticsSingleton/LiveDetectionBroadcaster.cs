using System.Collections.Concurrent;
using System.Threading.Channels;

namespace VmsAnalyticsSingleton;

// A geometry-only projection of a detection for the live overlay. Deliberately omits the crop JPEG
// that AnalyticsActivityEntry carries: the overlay only needs boxes + labels, so keeping multi-KB
// JPEG bytes queued in every subscriber's channel would waste memory for nothing.
public sealed record LiveDetection(
  string CameraId,
  long TrackId,
  string Kind,
  float Confidence,
  float BboxX,
  float BboxY,
  float BboxWidth,
  float BboxHeight,
  DateTime DetectedAt,
  string? PlateText,
  bool Persisted,
  string? ObjectId);

// In-process fan-out of live detections to subscribers (the "bbox over live video" overlay).
// DetectionStreamConsumer publishes every above-threshold detection here; each subscriber holds a
// small bounded channel. Best-effort and non-persistent: on overflow the oldest queued detection is
// dropped rather than blocking the consumer — a live overlay must never make the ingestion path
// wait, and a stale bbox is worthless anyway. A subscriber with cameraId == null receives all
// cameras (the frontend multiplexes one stream for the whole monitor).
public sealed class LiveDetectionBroadcaster
{
  private sealed record Subscription(string? CameraId, Channel<LiveDetection> Channel);

  private readonly ConcurrentDictionary<Guid, Subscription> _subs = new();

  public void Publish(LiveDetection entry)
  {
    foreach (var sub in _subs.Values)
    {
      if (sub.CameraId is null || sub.CameraId == entry.CameraId)
        sub.Channel.Writer.TryWrite(entry);
    }
  }

  // Yields live detections until the caller cancels. cameraId == null yields every camera. The
  // channel drops the oldest buffered entry when a slow reader lets it fill, so a stuck consumer
  // can't back-pressure Publish.
  public async IAsyncEnumerable<LiveDetection> Subscribe(
    string? cameraId, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
  {
    var channel = Channel.CreateBounded<LiveDetection>(new BoundedChannelOptions(256)
    {
      FullMode = BoundedChannelFullMode.DropOldest,
      SingleReader = true,
      SingleWriter = false,
    });
    var id = Guid.NewGuid();
    _subs[id] = new Subscription(cameraId, channel);
    try
    {
      await foreach (var entry in channel.Reader.ReadAllAsync(ct))
        yield return entry;
    }
    finally
    {
      _subs.TryRemove(id, out _);
    }
  }
}
