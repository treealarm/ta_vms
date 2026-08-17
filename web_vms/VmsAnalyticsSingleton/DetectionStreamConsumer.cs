using System.Globalization;
using Analytics;
using Google.Protobuf.WellKnownTypes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Vms.Cfg;
using Vms.Fs;

namespace VmsAnalyticsSingleton;

// Holds one long-lived StreamDetections call per analytics worker (reconnect/backoff per worker).
// For each DetectionEvent: threshold + per-(camera, track) debounce, then persist + crop file on
// the camera's storage volume + RetainEvent — all linked by one shared analytics-{guid} event id
// (watch_id == camera_id, 1:1 — see project plan).
public sealed class DetectionStreamConsumer(
  AnalyticsWorkerRegistry workers,
  AnalyticsWatchCache cache,
  DetectionDebouncer debouncer,
  AnalyticsActivityLog activityLog,
  LiveDetectionBroadcaster liveBroadcaster,
  WatchReconciler reconciler,
  CameraStorageResolver storageResolver,
  IDbContextFactory<AnalyticsDbContext> dbFactory,
  CameraServerCache serverCache,
  ServerPairRegistry serverPairRegistry,
  MetadataArchiveWriter metadataWriter,
  IObjectIdentityResolver objectResolver,
  ObjectSessionService sessions,
  VmsLog.EntityStatePublisher entityStatePublisher,
  ILogger<DetectionStreamConsumer> logger) : BackgroundService
{
  protected override async Task ExecuteAsync(CancellationToken stoppingToken)
  {
    await Task.WhenAll(workers.Workers.Select(w => ConsumeWorkerAsync(w.Key, w.Value, stoppingToken)));
  }

  private async Task ConsumeWorkerAsync(string workerAppId, IAnalyticsWorkerClient client, CancellationToken stoppingToken)
  {
    while (!stoppingToken.IsCancellationRequested)
    {
      try
      {
        await foreach (var evt in client.StreamDetectionsAsync(stoppingToken))
          await HandleAsync(evt, stoppingToken);
      }
      catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
      {
        break;
      }
      catch (Exception ex)
      {
        logger.LogWarning(ex, "Detection stream to worker '{Worker}' dropped, retrying in 5s", workerAppId);
        // A dropped stream is the liveness signal for that worker: if it restarted, its
        // in-memory watches are gone — reset the reconciler's view of it and keep
        // re-reconciling until the worker is reachable again and StartWatch succeeds.
        reconciler.ResetAssumedState(workerAppId);
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        try
        {
          await reconciler.ReconcileAsync(cache.Watches, stoppingToken);
        }
        catch (Exception reconcileEx)
        {
          logger.LogWarning(reconcileEx, "Post-reconnect reconcile failed (worker '{Worker}' still down?), will retry", workerAppId);
        }
      }
    }
  }

  private async Task HandleAsync(DetectionEvent evt, CancellationToken ct)
  {
    // watch_id == camera_id (1:1 model, see project plan).
    var cameraId = evt.WatchId;
    if (!cache.Watches.TryGetValue(cameraId, out var watch))
      return; // race with a StopWatch that already happened

    if (evt.Confidence < watch.MinConfidence)
      return;

    var detectedAt = evt.DetectedAt.ToDateTime();
    var persisted = debouncer.ShouldProcess(cameraId, evt.TrackId, evt.Kind, detectedAt) && watch.Armed;

    // Debug ring buffer sees every above-threshold event, including debounced ones — that's the
    // point of the panel: to show what the detector actually saw, not just what got stored.
    var activityEntry = new AnalyticsActivityEntry(
      detectedAt, cameraId, evt.TrackId, DetectionKinds.ToDbString(evt.Kind), evt.Confidence,
      evt.Bbox?.X ?? 0, evt.Bbox?.Y ?? 0, evt.Bbox?.Width ?? 0, evt.Bbox?.Height ?? 0,
      evt.HasRecognizedText ? evt.RecognizedText : null,
      evt.CropJpeg.ToByteArray(),
      persisted);
    activityLog.Record(activityEntry);

    // Feed the live overlay with every above-threshold detection (armed or not, debounced or not)
    // so bboxes track smoothly — gating this on `persisted` would make boxes flicker. A geometry-only
    // projection (no crop bytes) is published so the overlay channels stay small. object_id is a
    // read-only peek: it appears on the box once re-id has resolved this track (via a persisted
    // detection), letting the operator see whether a reappearance is the same physical object.
    liveBroadcaster.Publish(new LiveDetection(
      cameraId, evt.TrackId, DetectionKinds.ToDbString(evt.Kind), evt.Confidence,
      evt.Bbox?.X ?? 0, evt.Bbox?.Y ?? 0, evt.Bbox?.Width ?? 0, evt.Bbox?.Height ?? 0,
      detectedAt, evt.HasRecognizedText ? evt.RecognizedText : null, persisted,
      objectResolver.Peek(cameraId, evt.TrackId), Source: "cv"));

    // Recorded for the archive overlay before the debounce gate, for the same reason the live
    // overlay is fed before it: debounce exists to stop one object from minting an event every
    // frame, not to thin out geometry. Gating here would leave the archive with one box every ten
    // seconds, which is not an overlay.
    metadataWriter.Offer(new MetadataSample(
      cameraId, watch.StreamName ?? string.Empty, "cv", detectedAt,
      evt.TrackId, DetectionKinds.ToDbString(evt.Kind), evt.Confidence,
      evt.Bbox?.X ?? 0, evt.Bbox?.Y ?? 0, evt.Bbox?.Width ?? 0, evt.Bbox?.Height ?? 0,
      Label: null,
      PlateText: evt.HasRecognizedText ? evt.RecognizedText : null));

    if (!persisted)
      return;

    // Resolve the physical object (re-id), then pick its group label. VmsFs turns event_id into a
    // concrete episode (event_uuid) — extending the open one or opening a new one — so RetainEvent
    // must run before Persist, which stores the returned event_uuid on the detection row.
    var objectId = objectResolver.Resolve(
      evt.Kind, evt.Embedding, evt.EmbeddingIsFresh, cameraId, evt.TrackId,
      evt.HasRecognizedText ? evt.RecognizedText : null);
    var eventId = sessions.SelectEventId(objectId, detectedAt);

    logger.LogInformation(
      "Detection: camera={CameraId} track={TrackId} kind={Kind} confidence={Confidence:F2} plate={PlateText} event={EventId} object={ObjectId}",
      cameraId, evt.TrackId, evt.Kind, evt.Confidence,
      evt.HasRecognizedText ? evt.RecognizedText : "-", eventId, objectId);

    var (storageId, cropPath) = await SaveCropAsync(cameraId, evt, detectedAt, ct);
    var eventUuid = await RetainAsync(cameraId, evt, detectedAt, eventId, objectId, watch.TimeoutSeconds, ct);
    // If RetainEvent failed (FS unreachable) eventUuid is null: no episode was created and the clip
    // isn't retained either, so this detection has no episode to belong to. We still persist it (with
    // its event_id group) so the row isn't lost — it just can't appear in the per-episode details
    // panel, which is correct: there is no episode. The next successful detection opens one.
    await PersistAsync(cameraId, evt, detectedAt, eventId, eventUuid, objectId, storageId, cropPath, ct);

    entityStatePublisher.Publish("detection", $"{cameraId}/{evt.TrackId}", new
    {
      kind = evt.Kind.ToString(),
      confidence = evt.Confidence,
      detected_at = detectedAt,
      camera_id = cameraId,
      event_id = eventId,
      event_uuid = eventUuid,
      object_id = objectId,
      bbox = new { x = evt.Bbox?.X ?? 0, y = evt.Bbox?.Y ?? 0, width = evt.Bbox?.Width ?? 0, height = evt.Bbox?.Height ?? 0 },
    });
  }

  // Writes the crop JPEG onto the camera's primary storage volume (same referencing model as
  // clips: storage_id + path relative to the storage root). No storage assigned or write failure
  // never loses the detection — the row is just saved without a crop.
  private async Task<(string? StorageId, string? CropPath)> SaveCropAsync(
    string cameraId, DetectionEvent evt, DateTime detectedAt, CancellationToken ct)
  {
    if (evt.CropJpeg.IsEmpty)
      return (null, null);

    var storage = await storageResolver.GetPrimaryAsync(cameraId, ct);
    if (storage is null)
    {
      logger.LogWarning("Camera '{CameraId}' has no storage assigned — detection crop not saved", cameraId);
      return (null, null);
    }

    // Invariant culture is load-bearing: in custom DateTime formats '/' is the culture's date
    // separator placeholder, so without it the directory layout would silently vary per host locale.
    var relative = Path.Combine(
      "crops", cameraId,
      detectedAt.ToString("yyyy/MM/dd/HH", CultureInfo.InvariantCulture),
      $"{evt.TrackId}_{detectedAt.ToString("yyyyMMddTHHmmssfff", CultureInfo.InvariantCulture)}.jpg");
    var fullPath = Path.Combine(storage.RootPath, relative);

    try
    {
      Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
      await File.WriteAllBytesAsync(fullPath, evt.CropJpeg.ToByteArray(), ct);
      return (storage.StorageId, relative);
    }
    catch (Exception ex)
    {
      logger.LogWarning(ex, "Failed to write detection crop '{Path}'", fullPath);
      return (null, null);
    }
  }

  private async Task PersistAsync(
    string cameraId, DetectionEvent evt, DateTime detectedAt,
    string eventId, Guid? eventUuid, string objectId, string? storageId, string? cropPath, CancellationToken ct)
  {
    await using var db = await dbFactory.CreateDbContextAsync(ct);
    db.Detections.Add(new DbDetection
    {
      CameraId = cameraId,
      TrackId = evt.TrackId,
      Kind = DetectionKinds.ToDbString(evt.Kind),
      Confidence = evt.Confidence,
      DetectedAt = detectedAt,
      EventId = eventId,
      EventUuid = eventUuid,
      ObjectId = objectId,
      StorageId = storageId,
      CropPath = cropPath,
      PlateText = evt.HasRecognizedText ? evt.RecognizedText : null,
    });
    await db.SaveChangesAsync(ct);
  }

  // Returns the episode (event_uuid) the retention call opened or extended, or null on failure.
  private async Task<Guid?> RetainAsync(
    string cameraId, DetectionEvent evt, DateTime detectedAt, string eventId, string objectId,
    uint timeoutSeconds, CancellationToken ct)
  {
    try
    {
      var serverId = await serverCache.GetServerIdAsync(cameraId, ct);
      var fsClient = serverPairRegistry.GetFsClient(serverId);
      var request = new RetainEventRequest
      {
        EventId = eventId,
        CameraId = cameraId,
        AlarmStartTs = Timestamp.FromDateTime(DateTime.SpecifyKind(detectedAt, DateTimeKind.Utc)),
        // Per-watch, configured alongside the rest of the watch — the mirror of
        // OnvifAlarmFilter.timeout_seconds for the analytics source.
        TimeoutSeconds = Math.Max(1u, timeoutSeconds),
        ObjectId = objectId,
      };
      var status = await fsClient.RetainEventAsync(request, cancellationToken: ct);
      logger.LogInformation(
        "Camera {CameraId}: RetainEvent returned {Status} (kind={Kind}, confidence={Confidence:F2}, track={TrackId})",
        cameraId, status.Status, evt.Kind, evt.Confidence, evt.TrackId);
      return Guid.TryParse(status.EventUuid, out var uuid) ? uuid : null;
    }
    catch (Exception ex)
    {
      logger.LogWarning(ex, "RetainEvent failed for camera '{CameraId}' (kind={Kind}, confidence={Confidence:F2})", cameraId, evt.Kind, evt.Confidence);
      return null;
    }
  }

}
