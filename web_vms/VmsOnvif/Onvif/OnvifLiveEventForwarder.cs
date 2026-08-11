using System.Threading.Channels;
using Google.Protobuf.WellKnownTypes;
using Vms.Analytics;

namespace VmsOnvif.Onvif;

/// <summary>
/// Forwards ONVIF events to VmsAnalyticsSingleton so the monitor can show them over the live video.
/// </summary>
/// <remarks>
/// This is the answer for cameras whose analytics cannot draw anything. A cell-motion or tamper
/// engine produces no objects and no geometry — the event is the entire statement the camera is
/// able to make, so the overlay shows it as text where a box would go.
///
/// The route is the same one the metadata path takes, for the same reason: the browser holds one
/// stream for the whole grid, and only the single analytics instance can be its fan-in point —
/// events read from a per-node service would leave the (replicated) gateway merging one stream per
/// node. Nothing is stored here or there; this is a relay.
///
/// Events are queued and flushed as a batch, exactly like media_server's metadata client: one RPC
/// per event would put a round trip on the critical path of a subscription that also drives alarms,
/// and a camera announcing the same condition several times inside one window has nothing new to
/// say. Repeats of a (camera, topic) inside a flush collapse to the last one, so a clearing edge
/// that lands in the same window correctly wins over the start it cancels.
///
/// Best-effort: the queue is bounded and drops its oldest entries rather than blocking the receive
/// loop, and forwarding failures are logged, never raised. Losing a banner must not disturb the
/// subscription.
/// </remarks>
public sealed class OnvifLiveEventForwarder(
  VmsAnalyticsService.VmsAnalyticsServiceClient? analyticsClient,
  ILogger<OnvifLiveEventForwarder> logger) : BackgroundService
{
  private static readonly TimeSpan ForwardTimeout = TimeSpan.FromSeconds(2);

  // Short enough that a banner still reads as immediate, long enough that a burst costs one RPC.
  private static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(200);

  // Roughly a second of the noisiest camera park worth queueing; past that the link is not keeping
  // up and the oldest banners are the least worth showing.
  private const int QueueCapacity = 500;
  private const int MaxBatch = 128;

  private readonly Channel<LiveEvent> _queue = Channel.CreateBounded<LiveEvent>(
    new BoundedChannelOptions(QueueCapacity)
    {
      FullMode = BoundedChannelFullMode.DropOldest,
      SingleReader = true,
      SingleWriter = false,
    });

  public bool Enabled => analyticsClient is not null;

  /// <summary>
  /// Queues one event for the monitor. Safe from any thread and never blocks — the caller is the
  /// pull-point receive loop, which must not wait on anything downstream of it.
  /// </summary>
  public void Forward(string cameraId, string topic, bool active)
  {
    if (analyticsClient is null || string.IsNullOrEmpty(cameraId))
      return;

    _queue.Writer.TryWrite(new LiveEvent
    {
      CameraId   = cameraId,
      Topic      = topic ?? string.Empty,
      Label      = LabelFrom(topic),
      Active     = active,
      ReceivedAt = Timestamp.FromDateTime(DateTime.UtcNow),
    });
  }

  protected override async Task ExecuteAsync(CancellationToken ct)
  {
    if (analyticsClient is null)
      return;

    // Keyed by (camera, topic) so a condition announced repeatedly inside one window is sent once.
    var pending = new Dictionary<(string Camera, string Topic), LiveEvent>();

    while (!ct.IsCancellationRequested)
    {
      try
      {
        if (!await _queue.Reader.WaitToReadAsync(ct))
          return;

        // Let a burst gather before sending, rather than racing the first event out of the door.
        await Task.Delay(FlushInterval, ct);

        while (pending.Count < MaxBatch && _queue.Reader.TryRead(out var evt))
          pending[(evt.CameraId, evt.Topic)] = evt;

        if (pending.Count == 0)
          continue;

        var batch = new OnvifEventBatch();
        batch.Events.AddRange(pending.Values);
        pending.Clear();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(ForwardTimeout);
        await analyticsClient.PushOnvifEventsAsync(batch, cancellationToken: cts.Token);
      }
      catch (OperationCanceledException) when (ct.IsCancellationRequested)
      {
        return;
      }
      catch (Exception ex)
      {
        // Not retried: a banner that arrives late is worth less than the ones behind it.
        pending.Clear();
        logger.LogDebug(ex, "Failed to forward ONVIF events to the analytics service");
      }
    }
  }

  /// <summary>
  /// The last segment of the topic — "tns1:RuleEngine/CellMotionDetector/Motion" becomes "Motion".
  /// Topics are vendor-extensible, so anything unparseable falls back to the topic itself rather
  /// than to a placeholder: an operator can still match it against the alarm filters.
  /// </summary>
  private static string LabelFrom(string? topic)
  {
    if (string.IsNullOrWhiteSpace(topic))
      return "Event";

    var last = topic.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? topic;
    // Drop a namespace prefix on a single-segment topic ("tns1:Something").
    var colon = last.LastIndexOf(':');
    return colon >= 0 && colon < last.Length - 1 ? last[(colon + 1)..] : last;
  }
}
