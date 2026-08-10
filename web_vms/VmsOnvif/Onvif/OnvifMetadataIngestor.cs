using Google.Protobuf.WellKnownTypes;
using Vms.Analytics;
using Vms.Onvif;

namespace VmsOnvif.Onvif;

/// <summary>
/// Turns the raw Scene Description documents media_server reassembles off a camera's RTSP metadata
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
  ILogger<OnvifMetadataIngestor> logger)
{
  // media_server already coalesces documents over ~200 ms, so a batch arrives here whole and is
  // forwarded whole — no second queue on this side.
  private static readonly TimeSpan ForwardTimeout = TimeSpan.FromSeconds(2);

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

      // The document's own UtcTime is ignored: camera clocks are frequently hours off, and this
      // timestamp only ever feeds an overlay that ages boxes out after a couple of seconds.
      var detectedAt = document.ReceivedAt ?? Timestamp.FromDateTime(DateTime.UtcNow);

      foreach (var obj in objects)
      {
        request.Detections.Add(new OnvifDetection
        {
          CameraId   = document.CameraId,
          TrackId    = obj.TrackId,
          Kind       = ToProtoKind(obj.Kind),
          Confidence = obj.Confidence,
          Bbox       = new Vms.Analytics.BoundingBox
          {
            X = obj.BboxX, Y = obj.BboxY, Width = obj.BboxWidth, Height = obj.BboxHeight,
          },
          DetectedAt = detectedAt,
        });
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
    _               => DetectionKind.Unspecified,
  };
}
