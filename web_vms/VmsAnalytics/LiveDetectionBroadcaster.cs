using System.Collections.Concurrent;
using System.Threading.Channels;

namespace VmsAnalytics;

// In-process fan-out of live detections to per-camera subscribers (the "bbox over live video"
// overlay). DetectionStreamConsumer publishes every above-threshold event here; each subscriber
// holds a small bounded channel filtered by camera_id. Best-effort and non-persistent: on overflow
// the oldest queued detection is dropped rather than blocking the consumer — a live overlay must
// never make the ingestion path wait, and a stale bbox is worthless anyway.
public sealed class LiveDetectionBroadcaster
{
  private sealed record Subscription(string CameraId, Channel<AnalyticsActivityEntry> Channel);

  private readonly ConcurrentDictionary<Guid, Subscription> _subs = new();

  public void Publish(AnalyticsActivityEntry entry)
  {
    foreach (var sub in _subs.Values)
    {
      if (sub.CameraId == entry.CameraId)
        sub.Channel.Writer.TryWrite(entry);
    }
  }

  // Yields live detections for one camera until the caller cancels. The channel drops the oldest
  // buffered entry when a slow reader lets it fill, so a stuck consumer can't back-pressure Publish.
  public async IAsyncEnumerable<AnalyticsActivityEntry> Subscribe(
    string cameraId, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
  {
    var channel = Channel.CreateBounded<AnalyticsActivityEntry>(new BoundedChannelOptions(64)
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
