using System.Xml.Linq;
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
/// Best-effort: forwarding runs off the subscription thread and its failures are logged, never
/// raised. Losing a banner must not disturb the subscription that also drives alarms.
/// </remarks>
public sealed class OnvifLiveEventForwarder(
  VmsAnalyticsService.VmsAnalyticsServiceClient? analyticsClient,
  ILogger<OnvifLiveEventForwarder> logger)
{
  private static readonly TimeSpan ForwardTimeout = TimeSpan.FromSeconds(2);

  public bool Enabled => analyticsClient is not null;

  /// <summary>
  /// Queues one event for the monitor. Returns immediately — the caller is the pull-point receive
  /// loop, which must not wait on anything downstream of it.
  /// </summary>
  public void Forward(string cameraId, string topic, string payloadXml)
  {
    if (analyticsClient is null || string.IsNullOrEmpty(cameraId))
      return;

    var evt = new LiveEvent
    {
      CameraId   = cameraId,
      Topic      = topic ?? string.Empty,
      Label      = LabelFrom(topic),
      Active     = IsActiveState(payloadXml),
      ReceivedAt = Timestamp.FromDateTime(DateTime.UtcNow),
    };

    _ = Task.Run(async () =>
    {
      try
      {
        var batch = new OnvifEventBatch();
        batch.Events.Add(evt);
        using var cts = new CancellationTokenSource(ForwardTimeout);
        await analyticsClient.PushOnvifEventsAsync(batch, cancellationToken: cts.Token);
      }
      catch (Exception ex)
      {
        logger.LogDebug(ex,
          "Failed to forward ONVIF event to the analytics service for camera {CameraId}", cameraId);
      }
    });
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

  /// <summary>
  /// Whether the condition is starting or clearing. Shared with the alarm path, which must not
  /// treat "motion stopped" as a reason to start recording. ONVIF state events carry it as a boolean
  /// SimpleItem under tt:Data — the item's name varies by rule ("IsMotion", "IsTamper", "State"),
  /// so the value is what is matched on, not the name. Events with no boolean at all are
  /// momentary, and reported as active so they show up.
  /// </summary>
  public static bool IsActiveState(string? payloadXml)
  {
    if (string.IsNullOrWhiteSpace(payloadXml))
      return true;

    try
    {
      var doc = XDocument.Parse(payloadXml);
      foreach (var item in doc.Descendants().Where(e => e.Name.LocalName == "SimpleItem"))
      {
        var value = (string?)item.Attribute("Value");
        if (bool.TryParse(value, out var parsed))
          return parsed;
      }
    }
    catch (System.Xml.XmlException)
    {
      // Malformed payload — treat as a momentary event rather than losing it.
    }

    return true;
  }

  /// <summary>
  /// Identifies which source the event is about — the tt:Source items, joined. On a multi-channel
  /// encoder two channels report the same topic, and only these tell their alarms apart. Empty for
  /// events that name no source, which is correct: they are all about the device as a whole.
  /// </summary>
  public static string SourceKey(string? payloadXml)
  {
    if (string.IsNullOrWhiteSpace(payloadXml))
      return string.Empty;

    try
    {
      var doc = XDocument.Parse(payloadXml);
      var source = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Source");
      if (source is null)
        return string.Empty;

      return string.Join(",", source.Descendants()
        .Where(e => e.Name.LocalName == "SimpleItem")
        .Select(e => $"{(string?)e.Attribute("Name")}={(string?)e.Attribute("Value")}")
        .Order(StringComparer.Ordinal));
    }
    catch (System.Xml.XmlException)
    {
      return string.Empty;
    }
  }
}
