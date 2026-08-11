using System.Collections.Concurrent;
using System.Threading.Channels;

namespace VmsAnalyticsSingleton;

// One ONVIF event on its way to the monitor. No geometry: cameras whose analytics is cell motion or
// tamper have nothing to draw, and the event itself is the whole statement.
public sealed record LiveCameraEvent(
  string CameraId,
  string Topic,
  string Label,
  bool Active,
  DateTime ReceivedAt);

// In-process fan-out of ONVIF events to the live monitor, the counterpart of
// LiveDetectionBroadcaster and deliberately built the same way: bounded per-subscriber channels
// that drop their oldest entry rather than block, because the overlay must never make the service
// forwarding events wait on a slow browser.
//
// Kept separate from LiveDetectionBroadcaster rather than merged into it: a detection is geometry
// with a lifetime measured in a second, an event is a state change with no shape at all, and the
// two are consumed by different overlays. Sharing a channel would mean every subscriber filtering
// out the half it does not want.
public sealed class LiveEventBroadcaster
{
  private sealed record Subscription(string? CameraId, Channel<LiveCameraEvent> Channel);

  private readonly ConcurrentDictionary<Guid, Subscription> _subs = new();

  public void Publish(LiveCameraEvent entry)
  {
    foreach (var sub in _subs.Values)
    {
      if (sub.CameraId is null || sub.CameraId == entry.CameraId)
        sub.Channel.Writer.TryWrite(entry);
    }
  }

  // Yields events until the caller cancels. cameraId == null yields every camera.
  public async IAsyncEnumerable<LiveCameraEvent> Subscribe(
    string? cameraId, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
  {
    var channel = Channel.CreateBounded<LiveCameraEvent>(new BoundedChannelOptions(128)
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
