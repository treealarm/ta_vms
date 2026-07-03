using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Mvc;
using Vms.Analytics;
using web_vms.Types.Dto;

namespace web_vms;

// Read side of the detection gallery: rows from ta_analytics + crop JPEGs from the storage
// volumes, linked to retention events by event_id (analytics-{guid}). Deliberately not
// AdminOnly — the Events tab is an operator view, same access model as FSController.
[ApiController]
[Route("api/analytics/detections")]
public class AnalyticsDetectionsController(
  VmsAnalyticsService.VmsAnalyticsServiceClient _analyticsClient) : ControllerBase
{
  [HttpGet]
  public async Task<IActionResult> List(
    [FromQuery] string? eventId, [FromQuery] string? cameraId,
    [FromQuery] string? from, [FromQuery] string? to,
    [FromQuery] int? limit, CancellationToken ct)
  {
    var request = new ListDetectionsRequest { Limit = (uint)Math.Clamp(limit ?? 50, 1, 500) };
    if (!string.IsNullOrWhiteSpace(eventId)) request.EventId = eventId;
    if (!string.IsNullOrWhiteSpace(cameraId)) request.CameraId = cameraId;
    if (DateTime.TryParse(from, null, System.Globalization.DateTimeStyles.RoundtripKind, out var fromTs))
      request.FromTs = Timestamp.FromDateTime(fromTs.ToUniversalTime());
    if (DateTime.TryParse(to, null, System.Globalization.DateTimeStyles.RoundtripKind, out var toTs))
      request.ToTs = Timestamp.FromDateTime(toTs.ToUniversalTime());

    var response = await _analyticsClient.ListDetectionsAsync(request, cancellationToken: ct);
    return Ok(response.Detections.Select(d => new AnalyticsDetectionDto
    {
      Id         = d.Id,
      EventId    = d.EventId,
      CameraId   = d.CameraId,
      TrackId    = d.TrackId,
      Kind       = KindToString(d.Kind),
      Confidence = d.Confidence,
      DetectedAt = d.DetectedAt.ToDateTime(),
      PlateText  = d.HasPlateText ? d.PlateText : null,
      HasCrop    = d.HasCrop,
    }));
  }

  [HttpGet("{id:long}/crop")]
  public async Task<IActionResult> GetCrop(long id, CancellationToken ct)
  {
    var response = await _analyticsClient.GetDetectionCropAsync(
      new GetDetectionCropRequest { Id = id }, cancellationToken: ct);
    if (response.Jpeg.IsEmpty)
      return NotFound();

    return File(response.Jpeg.ToByteArray(), "image/jpeg");
  }

  private static string KindToString(DetectionKind kind) => kind switch
  {
    DetectionKind.Person => "person",
    DetectionKind.Face => "face",
    DetectionKind.Vehicle => "vehicle",
    DetectionKind.LicensePlate => "license_plate",
    _ => "unknown",
  };
}
