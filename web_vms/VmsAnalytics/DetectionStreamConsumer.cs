using Analytics;
using Google.Protobuf.WellKnownTypes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Vms.Cfg;
using Vms.Fs;

namespace VmsAnalytics;

// Holds one long-lived StreamDetections call to video_a (reconnect/backoff on failure). For
// each DetectionEvent: threshold + per-(watch_id, track_id) debounce, then fan out to every
// currently-armed camera linked to that watch — persist + RetainEvent per camera.
public sealed class DetectionStreamConsumer(
  IAnalyticsWorkerClient workerClient,
  AnalyticsWatchCache cache,
  DetectionDebouncer debouncer,
  IDbContextFactory<AnalyticsDbContext> dbFactory,
  CfgService.CfgServiceClient cfgClient,
  ServerPairRegistry serverPairRegistry,
  VmsLog.EntityStatePublisher entityStatePublisher,
  ILogger<DetectionStreamConsumer> logger) : BackgroundService
{
  private static readonly uint RetainTimeoutSeconds = RequireUint("ANALYTICS_RETAIN_TIMEOUT_SECONDS");

  protected override async Task ExecuteAsync(CancellationToken stoppingToken)
  {
    while (!stoppingToken.IsCancellationRequested)
    {
      try
      {
        await foreach (var evt in workerClient.StreamDetectionsAsync(stoppingToken))
          await HandleAsync(evt, stoppingToken);
      }
      catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
      {
        break;
      }
      catch (Exception ex)
      {
        logger.LogWarning(ex, "DetectionStreamConsumer stream error, retrying in 5s");
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
      }
    }
  }

  private async Task HandleAsync(DetectionEvent evt, CancellationToken ct)
  {
    if (!cache.Watches.TryGetValue(evt.WatchId, out var watch))
      return; // race with a StopWatch that already happened

    if (evt.Confidence < watch.MinConfidence)
      return;

    var detectedAt = evt.DetectedAt.ToDateTime();
    if (!debouncer.ShouldProcess(evt.WatchId, evt.TrackId, detectedAt))
      return;

    var armedCameraIds = watch.Cameras.Where(c => c.Armed).Select(c => c.CameraId).ToList();
    if (armedCameraIds.Count == 0)
      return;

    logger.LogInformation(
      "Detection: watch={WatchId} track={TrackId} kind={Kind} confidence={Confidence:F2} plate={PlateText} cameras={CameraIds}",
      evt.WatchId, evt.TrackId, evt.Kind, evt.Confidence,
      evt.HasRecognizedText ? evt.RecognizedText : "-",
      string.Join(",", armedCameraIds));

    foreach (var cameraId in armedCameraIds)
    {
      await PersistAsync(cameraId, evt, detectedAt, ct);
      await RetainAsync(cameraId, evt, detectedAt, ct);
    }

    entityStatePublisher.Publish("detection", $"{evt.WatchId}/{evt.TrackId}", new
    {
      kind = evt.Kind.ToString(),
      confidence = evt.Confidence,
      detected_at = detectedAt,
      camera_ids = armedCameraIds,
    });
  }

  private async Task PersistAsync(string cameraId, DetectionEvent evt, DateTime detectedAt, CancellationToken ct)
  {
    await using var db = await dbFactory.CreateDbContextAsync(ct);
    db.Detections.Add(new DbDetection
    {
      CameraId = cameraId,
      WatchId = Guid.Parse(evt.WatchId),
      TrackId = evt.TrackId,
      Kind = KindToDbString(evt.Kind),
      Confidence = evt.Confidence,
      DetectedAt = detectedAt,
      CropRef = string.IsNullOrEmpty(evt.CropRef) ? null : evt.CropRef,
      PlateText = evt.HasRecognizedText ? evt.RecognizedText : null,
    });
    await db.SaveChangesAsync(ct);
  }

  private async Task RetainAsync(string cameraId, DetectionEvent evt, DateTime detectedAt, CancellationToken ct)
  {
    try
    {
      var camResp = await cfgClient.GetCameraAsync(new SourceRequest { SourceId = cameraId }, cancellationToken: ct);
      var serverId = camResp.Found ? camResp.Setup.ServerId : null;
      var fsClient = serverPairRegistry.GetFsClient(serverId);
      var request = new RetainEventRequest
      {
        EventId = $"analytics-{Guid.NewGuid()}",
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

  private static string KindToDbString(DetectionKind kind) => kind switch
  {
    DetectionKind.Person => "person",
    DetectionKind.Face => "face",
    DetectionKind.Vehicle => "vehicle",
    DetectionKind.LicensePlate => "license_plate",
    _ => "unknown",
  };

  private static uint RequireUint(string name)
  {
    var raw = Environment.GetEnvironmentVariable(name)
      ?? throw new InvalidOperationException($"Required environment variable '{name}' is not set");
    return uint.TryParse(raw, out var value)
      ? value
      : throw new InvalidOperationException($"Required environment variable '{name}' must be a non-negative integer");
  }
}
