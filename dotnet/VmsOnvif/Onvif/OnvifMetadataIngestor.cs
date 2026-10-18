using Google.Protobuf.WellKnownTypes;
using Vms.Analytics;
using Vms.Onvif;

namespace VmsOnvif.Onvif;

/// <summary>
/// Turns the raw Scene Description documents video_srv reassembles off a camera's RTSP metadata
/// track into normalized boxes, and hands them to VmsAnalyticsSingleton for the live overlay.
/// </summary>
/// <remarks>
/// This lives in VmsOnvif because the ONVIF schema is ONVIF's business: everything else that reads
/// the vendor's XML is here, and being node-local means parsing spreads across nodes instead of
/// converging on the single analytics instance. Only the finished geometry crosses the wire.
///
/// Stateless by construction — parse, forward, forget. Nothing is cached, stored, or accumulated
/// between calls, so this adds no state to a service whose ONVIF subscriptions already pin it to
/// one instance per node.
///
/// Forwarding is optional: with DAPR_ANALYTICS_APP_ID unset there is no analytics service to feed,
/// and documents are dropped without touching the parser. Everything else in VmsOnvif keeps working.
/// </remarks>
public sealed class OnvifMetadataIngestor(
  VmsAnalyticsService.VmsAnalyticsServiceClient? analyticsClient,
  OnvifMetadataGateCache gate,
  ILogger<OnvifMetadataIngestor> logger)
{
  // video_srv already coalesces documents over ~200 ms, so a batch arrives here whole and is
  // forwarded whole — no second queue on this side.
  private static readonly TimeSpan ForwardTimeout = TimeSpan.FromSeconds(2);

  // Diagnostics for the one question this path always raises — "the camera says it supports this,
  // so why are there no boxes?". Logged once per camera per state change rather than per document:
  // a chatty camera would otherwise bury the log.
  private enum Seen { None, Classified, Unclassified, DocumentsOnly }

  // Enough of the document to see how a firmware lays out its classification, short enough that a
  // chatty camera cannot flood the log with it.
  private const int SampleChars = 4000;
  private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Seen> _seen = new();

  private void Report(string cameraId, IReadOnlyList<OnvifSceneObject> objects, string xml)
  {
    var state = objects.Count == 0
      ? Seen.DocumentsOnly
      : objects.Any(o => o.Kind != "unknown") ? Seen.Classified : Seen.Unclassified;

    if (_seen.TryGetValue(cameraId, out var previous) && previous == state)
      return;
    _seen[cameraId] = state;

    if (state == Seen.Classified)
    {
      logger.LogInformation(
        "ONVIF metadata: camera {CameraId} is reporting scene objects", cameraId);
    }
    else if (state == Seen.Unclassified)
    {
      // Boxes arrive but nothing says what they are. Every classification route ONVIF defines is
      // already read (tt:Class, tt:Extension/tt:OtherTypes, and the shape of tt:Appearance), so a
      // firmware landing here is putting the class somewhere non-standard. The document itself is
      // the only way to find out where, hence the sample.
      logger.LogWarning(
        "ONVIF metadata: camera {CameraId} reports {Count} object(s) but no class this build "
        + "recognises — they will draw as \"unknown\". Document sample: {Xml}",
        cameraId, objects.Count, Truncate(xml));
    }
    else
    {
      // The metadata track is flowing but describes no objects. Almost always one of: analytics
      // switched off on the metadata configuration, no analytics configuration attached to the
      // streamed profile, or no analytics module configured to run — all of them fixable from the
      // Metadata tab, none of them visible without saying so here.
      logger.LogWarning(
        "ONVIF metadata: camera {CameraId} streams metadata but reports no scene objects — check "
        + "that Analytics is enabled on the metadata configuration, that an analytics configuration "
        + "is attached to the streamed profile, and that an analytics module is configured. "
        + "Document sample: {Xml}",
        cameraId, Truncate(xml));
    }
  }

  private static string Truncate(string xml) =>
    xml.Length <= SampleChars ? xml : xml[..SampleChars] + "…";

  public bool ForwardingEnabled => analyticsClient is not null;

  public async Task IngestAsync(MetadataDocumentBatch batch, CancellationToken ct)
  {
    if (analyticsClient is null)
      return;

    var request = new OnvifDetectionBatch();

    foreach (var document in batch.Documents)
    {
      if (string.IsNullOrEmpty(document.CameraId) || string.IsNullOrEmpty(document.Xml))
        continue;

      // Dropped before the parse, which is the point of the setting: a camera that reports
      // boxes without saying what they are is worse than one that reports nothing once a real
      // detector runs beside it, and the ROI pass would prefer this source over the classified
      // one purely because its timestamps are better. The camera keeps sending either way --
      // switching it off on the device is a separate control, in the camera's ONVIF settings.
      if (await gate.IsDisabledAsync(document.CameraId, ct))
        continue;

      IReadOnlyList<OnvifSceneObject> objects;
      try
      {
        objects = OnvifSceneParser.Parse(document.Xml, document.FrameWidth, document.FrameHeight);
      }
      catch (Exception ex)
      {
        // A firmware laying the schema out its own way must not take down the ingestion of every
        // other camera in the batch.
        logger.LogWarning(ex,
          "Failed to parse ONVIF metadata document from camera {CameraId}", document.CameraId);
        continue;
      }

      Report(document.CameraId, objects, document.Xml);
      if (objects.Count == 0)
        continue;

      // The document's own UtcTime is ignored: camera clocks are frequently hours off, and this
      // timestamp only ever feeds an overlay that ages boxes out after a couple of seconds.
      var detectedAt = document.ReceivedAt ?? Timestamp.FromDateTime(DateTime.UtcNow);

      foreach (var obj in objects)
      {
        var detection = new OnvifDetection
        {
          CameraId   = document.CameraId,
          // Carried through for the archive: clips are split per stream, so geometry recorded
          // against the wrong one would draw sub-stream boxes over main-stream video. The live
          // overlay ignores it — it keys boxes by camera.
          StreamName = document.StreamName ?? string.Empty,
          TrackId    = obj.TrackId,
          Kind       = ToProtoKind(obj.Kind),
          Confidence = obj.Confidence,
          Bbox       = new Vms.Analytics.BoundingBox
          {
            X = obj.BboxX, Y = obj.BboxY, Width = obj.BboxWidth, Height = obj.BboxHeight,
          },
          DetectedAt = detectedAt,
        };
        if (obj.PlateText is not null)
          detection.PlateText = obj.PlateText;
        if (obj.Label is not null)
          detection.Label = obj.Label;
        request.Detections.Add(detection);
      }
    }

    if (request.Detections.Count == 0)
      return;

    try
    {
      using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
      cts.CancelAfter(ForwardTimeout);
      await analyticsClient.PushOnvifDetectionsAsync(
        request, cancellationToken: cts.Token);
    }
    catch (Exception ex)
    {
      // Not retried: the overlay wants fresh boxes, and re-sending a batch that is already a couple
      // of hundred milliseconds old would only delay the ones behind it.
      logger.LogWarning(ex, "Failed to forward {Count} ONVIF detection(s) to the analytics service",
        request.Detections.Count);
    }
  }

  private static DetectionKind ToProtoKind(string kind) => kind switch
  {
    "person"        => DetectionKind.Person,
    "face"          => DetectionKind.Face,
    "vehicle"       => DetectionKind.Vehicle,
    "license_plate" => DetectionKind.LicensePlate,
    "animal"        => DetectionKind.Animal,
    _               => DetectionKind.Unspecified,
  };
}
