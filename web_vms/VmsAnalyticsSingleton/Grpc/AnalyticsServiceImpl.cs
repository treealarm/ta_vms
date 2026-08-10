using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Vms.Analytics;
// The local ring-buffer record VmsAnalyticsSingleton.AnalyticsActivityEntry shadows the proto message
// of the same name inside this namespace — alias the proto one explicitly.
using ProtoActivityEntry = Vms.Analytics.AnalyticsActivityEntry;

namespace VmsAnalyticsSingleton;

public sealed class AnalyticsServiceImpl(
  IDbContextFactory<AnalyticsDbContext> dbFactory,
  AnalyticsActivityLog activityLog,
  LiveDetectionBroadcaster liveBroadcaster,
  CameraStorageResolver storageResolver)
  : VmsAnalyticsService.VmsAnalyticsServiceBase
{
  // Boxes the camera reported itself, already parsed and normalized by VmsOnvif (which owns the
  // ONVIF schema knowledge). Straight into the same fan-out the CV pipeline feeds — nothing is
  // stored, and the reply carries nothing, because the caller never retries.
  public override Task<PushOnvifDetectionsResponse> PushOnvifDetections(
    OnvifDetectionBatch request, ServerCallContext context)
  {
    foreach (var d in request.Detections)
    {
      liveBroadcaster.Publish(new LiveDetection(
        d.CameraId, d.TrackId, DetectionKinds.ToDbString(d.Kind), d.Confidence,
        d.Bbox?.X ?? 0, d.Bbox?.Y ?? 0, d.Bbox?.Width ?? 0, d.Bbox?.Height ?? 0,
        d.DetectedAt?.ToDateTime() ?? DateTime.UtcNow,
        PlateText: null, Persisted: false, ObjectId: null, Source: "onvif"));
    }
    return Task.FromResult(new PushOnvifDetectionsResponse());
  }

  public override Task<GetAnalyticsActivityResponse> GetAnalyticsActivity(
    GetAnalyticsActivityRequest request, ServerCallContext context)
  {
    var limit = request.Limit > 0 ? (int)request.Limit : 50;
    var response = new GetAnalyticsActivityResponse();
    foreach (var e in activityLog.GetRecent(limit))
    {
      response.Events.Add(ToProto(e));
    }
    return Task.FromResult(response);
  }

  public override async Task StreamCameraDetections(
    StreamCameraDetectionsRequest request,
    IServerStreamWriter<ProtoActivityEntry> responseStream,
    ServerCallContext context)
  {
    // Empty camera_id means "all cameras" — the frontend multiplexes one stream for the whole
    // monitor rather than opening one connection per camera. crop_jpeg is never populated (the
    // broadcaster already carries a geometry-only projection).
    var cameraId = string.IsNullOrEmpty(request.CameraId) ? null : request.CameraId;
    await foreach (var e in liveBroadcaster.Subscribe(cameraId, context.CancellationToken))
    {
      var entry = new ProtoActivityEntry
      {
        CameraId   = e.CameraId,
        TrackId    = e.TrackId,
        Kind       = DetectionKinds.Parse(e.Kind),
        Confidence = e.Confidence,
        Bbox       = new BoundingBox { X = e.BboxX, Y = e.BboxY, Width = e.BboxWidth, Height = e.BboxHeight },
        DetectedAt = Timestamp.FromDateTime(DateTime.SpecifyKind(e.DetectedAt, DateTimeKind.Utc)),
        Persisted  = e.Persisted,
        Source     = e.Source,
      };
      if (e.PlateText is not null) entry.PlateText = e.PlateText;
      if (e.ObjectId is not null) entry.ObjectId = e.ObjectId;
      await responseStream.WriteAsync(entry);
    }
  }

  private static ProtoActivityEntry ToProto(AnalyticsActivityEntry e)
  {
    var entry = new ProtoActivityEntry
    {
      CameraId   = e.CameraId,
      TrackId    = e.TrackId,
      Kind       = DetectionKinds.Parse(e.Kind),
      Confidence = e.Confidence,
      Bbox       = new BoundingBox { X = e.BboxX, Y = e.BboxY, Width = e.BboxWidth, Height = e.BboxHeight },
      DetectedAt = Timestamp.FromDateTime(DateTime.SpecifyKind(e.DetectedAt, DateTimeKind.Utc)),
      CropJpeg   = Google.Protobuf.ByteString.CopyFrom(e.CropJpeg),
      Persisted  = e.Persisted,
      // The ring buffer only ever holds CV detections — ONVIF metadata is published straight to
      // the live overlay and never recorded here.
      Source     = "cv",
    };
    if (e.PlateText is not null) entry.PlateText = e.PlateText;
    return entry;
  }

  public override async Task<ListDetectionsResponse> ListDetections(
    ListDetectionsRequest request, ServerCallContext context)
  {
    await using var db = await dbFactory.CreateDbContextAsync(context.CancellationToken);
    var query = db.Detections.AsQueryable();

    if (request.HasCameraId && !string.IsNullOrWhiteSpace(request.CameraId))
      query = query.Where(d => d.CameraId == request.CameraId);
    // Caller's visible set, applied in SQL so a restricted user still gets full pages.
    if (request.CameraIds.Count > 0)
    {
      var visible = request.CameraIds.ToList();
      query = query.Where(d => visible.Contains(d.CameraId));
    }
    if (request.HasEventId && !string.IsNullOrWhiteSpace(request.EventId))
      query = query.Where(d => d.EventId == request.EventId);
    if (request.HasEventUuid && Guid.TryParse(request.EventUuid, out var eventUuid))
      query = query.Where(d => d.EventUuid == eventUuid);
    if (request.FromTs is not null)
    {
      var from = request.FromTs.ToDateTime();
      query = query.Where(d => d.DetectedAt >= from);
    }
    if (request.ToTs is not null)
    {
      var to = request.ToTs.ToDateTime();
      query = query.Where(d => d.DetectedAt <= to);
    }
    if (request.HasPlateText && !string.IsNullOrWhiteSpace(request.PlateText))
      query = query.Where(d => d.PlateText == request.PlateText);

    // Limit == 0 (proto default) must not mean "the whole table" — cap it server-side.
    query = query
      .OrderByDescending(d => d.DetectedAt)
      .Take(request.Limit > 0 ? (int)request.Limit : 100);

    var rows = await query.ToListAsync(context.CancellationToken);
    var response = new ListDetectionsResponse();
    response.Detections.AddRange(rows.Select(ToProto));
    return response;
  }

  public override async Task<GetDetectionCropResponse> GetDetectionCrop(
    GetDetectionCropRequest request, ServerCallContext context)
  {
    await using var db = await dbFactory.CreateDbContextAsync(context.CancellationToken);
    var row = await db.Detections.FindAsync([request.Id], context.CancellationToken);
    if (row is null)
      return new GetDetectionCropResponse();

    // Always report the owning camera, even when there is no crop to return: the gateway decides
    // whether this caller may see the detection at all before it looks at the bytes.
    var response = new GetDetectionCropResponse { CameraId = row.CameraId };

    if (row.StorageId is null || row.CropPath is null)
      return response;

    var root = await storageResolver.GetRootPathAsync(row.StorageId, context.CancellationToken);
    if (root is null)
      return response;

    var fullPath = Path.Combine(root, row.CropPath);
    if (!File.Exists(fullPath))
      return response;

    response.Jpeg = Google.Protobuf.ByteString.CopyFrom(
      await File.ReadAllBytesAsync(fullPath, context.CancellationToken));
    return response;
  }

  private static Detection ToProto(DbDetection row)
  {
    var detection = new Detection
    {
      Id = row.Id,
      CameraId = row.CameraId,
      TrackId = row.TrackId,
      Kind = DetectionKinds.Parse(row.Kind),
      Confidence = row.Confidence,
      DetectedAt = Timestamp.FromDateTime(DateTime.SpecifyKind(row.DetectedAt, DateTimeKind.Utc)),
      EventId = row.EventId ?? string.Empty,
      EventUuid = row.EventUuid?.ToString() ?? string.Empty,
      HasCrop = row.CropPath != null,
    };
    if (row.PlateText is not null) detection.PlateText = row.PlateText;
    if (row.ObjectId is not null) detection.ObjectId = row.ObjectId;
    return detection;
  }

}
