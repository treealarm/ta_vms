using System.Globalization;
using Analytics;
using Google.Protobuf.WellKnownTypes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Vms.Cfg;
using Vms.Fs;

namespace VmsAnalytics;

// Holds one long-lived StreamDetections call per analytics worker (reconnect/backoff per worker).
// For each DetectionEvent: threshold + per-(camera, track) debounce, then persist + crop file on
// the camera's storage volume + RetainEvent — all linked by one shared analytics-{guid} event id
// (watch_id == camera_id, 1:1 — see project plan).
public sealed class DetectionStreamConsumer(
  AnalyticsWorkerRegistry workers,
  AnalyticsWatchCache cache,
  DetectionDebouncer debouncer,
  AnalyticsActivityLog activityLog,
  WatchReconciler reconciler,
  CameraStorageResolver storageResolver,
  IDbContextFactory<AnalyticsDbContext> dbFactory,
  CfgService.CfgServiceClient cfgClient,
  ServerPairRegistry serverPairRegistry,
  VmsLog.EntityStatePublisher entityStatePublisher,
  ILogger<DetectionStreamConsumer> logger) : BackgroundService
{
  private static readonly uint RetainTimeoutSeconds = RequireUint("ANALYTICS_RETAIN_TIMEOUT_SECONDS");

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
    var persisted = debouncer.ShouldProcess(cameraId, evt.TrackId, detectedAt) && watch.Armed;

    // Debug ring buffer sees every above-threshold event, including debounced ones — that's the
    // point of the panel: to show what the detector actually saw, not just what got stored.
    activityLog.Record(new AnalyticsActivityEntry(
      detectedAt, cameraId, evt.TrackId, DetectionKinds.ToDbString(evt.Kind), evt.Confidence,
      evt.Bbox?.X ?? 0, evt.Bbox?.Y ?? 0, evt.Bbox?.Width ?? 0, evt.Bbox?.Height ?? 0,
      evt.HasRecognizedText ? evt.RecognizedText : null,
      evt.CropJpeg.ToByteArray(),
      persisted));

    if (!persisted)
      return;

    // One id links the retention event (ta_fs), the detection row and its crop file.
    var eventId = $"analytics-{Guid.NewGuid()}";

    logger.LogInformation(
      "Detection: camera={CameraId} track={TrackId} kind={Kind} confidence={Confidence:F2} plate={PlateText} event={EventId}",
      cameraId, evt.TrackId, evt.Kind, evt.Confidence,
      evt.HasRecognizedText ? evt.RecognizedText : "-", eventId);

    var (storageId, cropPath) = await SaveCropAsync(cameraId, evt, detectedAt, ct);
    await PersistAsync(cameraId, evt, detectedAt, eventId, storageId, cropPath, ct);
    await RetainAsync(cameraId, evt, detectedAt, eventId, ct);

    entityStatePublisher.Publish("detection", $"{cameraId}/{evt.TrackId}", new
    {
      kind = evt.Kind.ToString(),
      confidence = evt.Confidence,
      detected_at = detectedAt,
      camera_id = cameraId,
      event_id = eventId,
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
    string eventId, string? storageId, string? cropPath, CancellationToken ct)
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
      StorageId = storageId,
      CropPath = cropPath,
      PlateText = evt.HasRecognizedText ? evt.RecognizedText : null,
    });
    await db.SaveChangesAsync(ct);
  }

  private async Task RetainAsync(string cameraId, DetectionEvent evt, DateTime detectedAt, string eventId, CancellationToken ct)
  {
    try
    {
      var camResp = await cfgClient.GetCameraAsync(new SourceRequest { SourceId = cameraId }, cancellationToken: ct);
      var serverId = camResp.Found ? camResp.Setup.ServerId : null;
      var fsClient = serverPairRegistry.GetFsClient(serverId);
      var request = new RetainEventRequest
      {
        EventId = eventId,
        CameraId = cameraId,
        AlarmStartTs = Timestamp.FromDateTime(DateTime.SpecifyKind(detectedAt, DateTimeKind.Utc)),
        TimeoutSeconds = RetainTimeoutSeconds,
      };
      var status = await fsClient.RetainEventAsync(request, cancellationToken: ct);
      logger.LogInformation(
        "Camera {CameraId}: RetainEvent returned {Status} (kind={Kind}, confidence={Confidence:F2}, track={TrackId})",
        cameraId, status.Status, evt.Kind, evt.Confidence, evt.TrackId);
    }
    catch (Exception ex)
    {
      logger.LogWarning(ex, "RetainEvent failed for camera '{CameraId}' (kind={Kind}, confidence={Confidence:F2})", cameraId, evt.Kind, evt.Confidence);
    }
  }


  private static uint RequireUint(string name)
  {
    var raw = Environment.GetEnvironmentVariable(name)
      ?? throw new InvalidOperationException($"Required environment variable '{name}' is not set");
    return uint.TryParse(raw, out var value)
      ? value
      : throw new InvalidOperationException($"Required environment variable '{name}' must be a non-negative integer");
  }
}
