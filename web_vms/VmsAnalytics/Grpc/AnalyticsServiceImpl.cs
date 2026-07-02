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
  AnalyticsActivityLog activityLog)
  : VmsAnalyticsService.VmsAnalyticsServiceBase
{
  public override Task<GetAnalyticsActivityResponse> GetAnalyticsActivity(
    GetAnalyticsActivityRequest request, ServerCallContext context)
  {
    var limit = request.Limit > 0 ? (int)request.Limit : 50;
    var response = new GetAnalyticsActivityResponse();
    foreach (var e in activityLog.GetRecent(limit))
    {
      var entry = new ProtoActivityEntry
      {
        CameraId   = e.CameraId,
        TrackId    = e.TrackId,
        Kind       = ParseKind(e.Kind),
        Confidence = e.Confidence,
        Bbox       = new BoundingBox { X = e.BboxX, Y = e.BboxY, Width = e.BboxWidth, Height = e.BboxHeight },
        DetectedAt = Timestamp.FromDateTime(DateTime.SpecifyKind(e.DetectedAt, DateTimeKind.Utc)),
        CropJpeg   = Google.Protobuf.ByteString.CopyFrom(e.CropJpeg),
        Persisted  = e.Persisted,
      };
      if (e.PlateText is not null) entry.PlateText = e.PlateText;
      response.Events.Add(entry);
    }
    return Task.FromResult(response);
  }

  public override async Task<ListDetectionsResponse> ListDetections(
    ListDetectionsRequest request, ServerCallContext context)
  {
    await using var db = await dbFactory.CreateDbContextAsync(context.CancellationToken);
    var query = db.Detections.AsQueryable();

    if (!string.IsNullOrWhiteSpace(request.CameraId))
      query = query.Where(d => d.CameraId == request.CameraId);
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

    query = query.OrderByDescending(d => d.DetectedAt);
    if (request.Limit > 0)
      query = query.Take((int)request.Limit);

    var rows = await query.ToListAsync(context.CancellationToken);
    var response = new ListDetectionsResponse();
    response.Detections.AddRange(rows.Select(ToProto));
    return response;
  }

  private static Detection ToProto(DbDetection row)
  {
    var detection = new Detection
    {
      CameraId = row.CameraId,
      TrackId = row.TrackId,
      Kind = ParseKind(row.Kind),
      Confidence = row.Confidence,
      DetectedAt = Timestamp.FromDateTime(DateTime.SpecifyKind(row.DetectedAt, DateTimeKind.Utc)),
      CropRef = row.CropRef ?? string.Empty,
    };
    if (row.PlateText is not null) detection.PlateText = row.PlateText;
    return detection;
  }

  private static DetectionKind ParseKind(string kind) => kind switch
  {
    "person" => DetectionKind.Person,
    "face" => DetectionKind.Face,
    "vehicle" => DetectionKind.Vehicle,
    "license_plate" => DetectionKind.LicensePlate,
    _ => DetectionKind.Unspecified,
  };
}
