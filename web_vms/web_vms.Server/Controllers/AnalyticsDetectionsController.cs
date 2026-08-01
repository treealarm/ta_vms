using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Mvc;
using Vms.Analytics;
using web_vms.Authorization;
using web_vms.Types.Dto;

namespace web_vms;

// Read side of the detection gallery: rows from ta_analytics + crop JPEGs from the storage
// volumes, linked to retention events by event_id (analytics-{guid}). Deliberately not
// AdminOnly — the Events tab is an operator view, same access model as FSController.
[ApiController]
[Route("api/analytics/detections")]
public class AnalyticsDetectionsController(
  VmsAnalyticsService.VmsAnalyticsServiceClient _analyticsClient,
  ICameraAccessService _access) : ControllerBase
{
  [CameraAccess(CameraAction.ViewArchive, Source = CameraIdSource.Action)]
  [HttpGet]
  public async Task<IActionResult> List(
    [FromQuery] string? eventId, [FromQuery] string? eventUuid, [FromQuery] string? cameraId,
    [FromQuery] string? from, [FromQuery] string? to,
    [FromQuery] int? limit, CancellationToken ct)
  {
    var scope = await _access.ScopeAsync(User, CameraAction.ViewArchive, ct);
    if (!string.IsNullOrWhiteSpace(cameraId) && !scope.Contains(cameraId))
      return StatusCode(StatusCodes.Status403Forbidden);

    var request = new ListDetectionsRequest { Limit = (uint)Math.Clamp(limit ?? 50, 1, 500) };
    if (!string.IsNullOrWhiteSpace(eventId)) request.EventId = eventId;
    if (!string.IsNullOrWhiteSpace(eventUuid)) request.EventUuid = eventUuid;
    if (!string.IsNullOrWhiteSpace(cameraId)) request.CameraId = cameraId;
    // Pushed into the query rather than filtered afterwards, so a user with few of many cameras
    // still gets full pages instead of mostly-empty ones.
    if (!scope.All)
    {
      if (scope.IsEmpty)
        return Ok(Array.Empty<AnalyticsDetectionDto>());
      request.CameraIds.AddRange(scope.Ids);
    }
    if (DateTime.TryParse(from, null, System.Globalization.DateTimeStyles.RoundtripKind, out var fromTs))
      request.FromTs = Timestamp.FromDateTime(fromTs.ToUniversalTime());
    if (DateTime.TryParse(to, null, System.Globalization.DateTimeStyles.RoundtripKind, out var toTs))
      request.ToTs = Timestamp.FromDateTime(toTs.ToUniversalTime());

    var response = await _analyticsClient.ListDetectionsAsync(request, cancellationToken: ct);
    return Ok(response.Detections.Select(d => new AnalyticsDetectionDto
    {
      Id         = d.Id,
      EventId    = d.EventId,
      EventUuid  = d.EventUuid,
      ObjectId   = d.HasObjectId ? d.ObjectId : null,
      CameraId   = d.CameraId,
      TrackId    = d.TrackId,
      Kind       = AnalyticsKindMapper.ToStringId(d.Kind),
      Confidence = d.Confidence,
      DetectedAt = d.DetectedAt.ToDateTime(),
      PlateText  = d.HasPlateText ? d.PlateText : null,
      HasCrop    = d.HasCrop,
    }));
  }

  [CameraAccess(CameraAction.ViewArchive, Source = CameraIdSource.Action)]
  [HttpGet("{id:long}/crop")]
  public async Task<IActionResult> GetCrop(long id, CancellationToken ct)
  {
    var response = await _analyticsClient.GetDetectionCropAsync(
      new GetDetectionCropRequest { Id = id }, cancellationToken: ct);

    // Unknown detection and forbidden detection answer alike, so a 404 cannot be used to probe
    // which ids exist on cameras the caller may not see.
    if (string.IsNullOrEmpty(response.CameraId)
      || !await _access.IsAllowedAsync(User, response.CameraId, CameraAction.ViewArchive, ct))
      return NotFound();

    if (response.Jpeg.IsEmpty)
      return NotFound();

    return File(response.Jpeg.ToByteArray(), "image/jpeg");
  }

}
