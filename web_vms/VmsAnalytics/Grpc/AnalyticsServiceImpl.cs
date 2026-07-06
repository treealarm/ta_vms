using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Vms.Analytics;
// The local ring-buffer record VmsAnalytics.AnalyticsActivityEntry shadows the proto message
// of the same name inside this namespace — alias the proto one explicitly.
using ProtoActivityEntry = Vms.Analytics.AnalyticsActivityEntry;

namespace VmsAnalytics;

public sealed class AnalyticsServiceImpl(
  IDbContextFactory<AnalyticsDbContext> dbFactory,
  AnalyticsActivityLog activityLog,
  LiveDetectionBroadcaster liveBroadcaster,
  CameraStorageResolver storageResolver)
  : VmsAnalyticsService.VmsAnalyticsServiceBase
{
  public override Task<GetAnalyticsActivityResponse> GetAnalyticsActivity(
    GetAnalyticsActivityRequest request, ServerCallContext context)
  {
    var limit = request.Limit > 0 ? (int)request.Limit : 50;
    var response = new GetAnalyticsActivityResponse();
    foreach (var e in activityLog.GetRecent(limit))
    {
      var entry = ToProto(e, includeCrop: true);
      response.Events.Add(entry);
    }
    return Task.FromResult(response);
  }

  public override async Task StreamCameraDetections(
    StreamCameraDetectionsRequest request,
    IServerStreamWriter<ProtoActivityEntry> responseStream,
    ServerCallContext context)
  {
    // crop_jpeg is intentionally dropped here — the overlay only needs geometry + labels, and the
    // crop can be several KB per detection.
    await foreach (var e in liveBroadcaster.Subscribe(request.CameraId, context.CancellationToken))
      await responseStream.WriteAsync(ToProto(e, includeCrop: false));
  }

  private static ProtoActivityEntry ToProto(AnalyticsActivityEntry e, bool includeCrop)
  {
    var entry = new ProtoActivityEntry
    {
      CameraId   = e.CameraId,
      TrackId    = e.TrackId,
      Kind       = DetectionKinds.Parse(e.Kind),
      Confidence = e.Confidence,
      Bbox       = new BoundingBox { X = e.BboxX, Y = e.BboxY, Width = e.BboxWidth, Height = e.BboxHeight },
      DetectedAt = Timestamp.FromDateTime(DateTime.SpecifyKind(e.DetectedAt, DateTimeKind.Utc)),
      CropJpeg   = includeCrop ? Google.Protobuf.ByteString.CopyFrom(e.CropJpeg) : Google.Protobuf.ByteString.Empty,
      Persisted  = e.Persisted,
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
    if (request.HasEventId && !string.IsNullOrWhiteSpace(request.EventId))
      query = query.Where(d => d.EventId == request.EventId);
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
    if (row?.StorageId is null || row.CropPath is null)
      return new GetDetectionCropResponse();

    var root = await storageResolver.GetRootPathAsync(row.StorageId, context.CancellationToken);
    if (root is null)
      return new GetDetectionCropResponse();

    var fullPath = Path.Combine(root, row.CropPath);
    if (!File.Exists(fullPath))
      return new GetDetectionCropResponse();

    var bytes = await File.ReadAllBytesAsync(fullPath, context.CancellationToken);
    return new GetDetectionCropResponse { Jpeg = Google.Protobuf.ByteString.CopyFrom(bytes) };
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
      HasCrop = row.CropPath != null,
    };
    if (row.PlateText is not null) detection.PlateText = row.PlateText;
    return detection;
  }

}
