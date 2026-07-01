using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Vms.Analytics;

namespace VmsAnalytics;

public sealed class AnalyticsServiceImpl(IDbContextFactory<AnalyticsDbContext> dbFactory)
  : VmsAnalyticsService.VmsAnalyticsServiceBase
{
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
      WatchId = row.WatchId.ToString(),
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
