using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Mvc;
using Vms.Analytics;
using web_srv.Authorization;
using web_srv.Types.Dto;

namespace web_srv;

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

  // ACL is applied inside (probe camera + result camera filter via ScopeAsync), same as List.
  [CameraAccess(CameraAction.ViewArchive, Source = CameraIdSource.Action)]
  [HttpPost("search-similar")]
  public async Task<IActionResult> SearchSimilar([FromBody] SearchSimilarDetectionsRequestDto dto, CancellationToken ct)
  {
    if (dto is null)
      return BadRequest(new { code = "INVALID_REQUEST", message = "request body is required" });

    var hasDetection = dto.Detection is not null;
    var hasArchive = dto.Archive is not null;
    var hasImage = dto.Image is not null;
    if ((hasDetection ? 1 : 0) + (hasArchive ? 1 : 0) + (hasImage ? 1 : 0) != 1)
      return BadRequest(new { code = "INVALID_PROBE", message = "exactly one probe must be set" });
    if (dto.FromTs > dto.ToTs)
      return BadRequest(new { code = "INVALID_RANGE", message = "fromTs must be <= toTs" });

    var scope = await _access.ScopeAsync(User, CameraAction.ViewArchive, ct);
    var request = new SearchSimilarDetectionsRequest
    {
      FromTs = Timestamp.FromDateTime(dto.FromTs.ToUniversalTime()),
      ToTs = Timestamp.FromDateTime(dto.ToTs.ToUniversalTime()),
      Limit = (uint)Math.Clamp(dto.Limit <= 0 ? 100 : dto.Limit, 1, 500),
    };
    if (dto.MinScore.HasValue)
      request.MinScore = dto.MinScore.Value;

    var requestedCameras = dto.CameraIds
      .Where(x => !string.IsNullOrWhiteSpace(x))
      .Distinct()
      .ToList();
    if (dto.CameraIds.Count > 0)
    {
      // Caller asked to restrict the search. An empty set after ACL (or only whitespace) must
      // stay empty — analytics treats a missing camera_ids filter as "every camera".
      if (!scope.All)
        requestedCameras = requestedCameras.Where(scope.Contains).ToList();
      if (requestedCameras.Count == 0)
        return Ok(Array.Empty<SimilarDetectionHitDto>());
      request.CameraIds.AddRange(requestedCameras);
    }
    else if (!scope.All)
    {
      if (scope.IsEmpty)
        return Ok(Array.Empty<SimilarDetectionHitDto>());
      request.CameraIds.AddRange(scope.Ids);
    }

    if (hasDetection)
    {
      var detectionId = dto.Detection!.DetectionId;
      request.DetectionProbe = new DetectionIdProbe { DetectionId = detectionId };
      if (!scope.All)
      {
        // Same collapsing as GetCrop: a detection on a camera this caller cannot see must not
        // distinguish 404 / 422 / "found" from an id that does not exist.
        var crop = await _analyticsClient.GetDetectionCropAsync(
          new GetDetectionCropRequest { Id = detectionId }, cancellationToken: ct);
        if (string.IsNullOrEmpty(crop.CameraId) || !scope.Contains(crop.CameraId))
          return NotFound(new { code = "PROBE_NOT_FOUND", message = "Selected probe was not found." });
      }
    }
    else if (hasArchive)
    {
      var archive = dto.Archive!;
      if (string.IsNullOrWhiteSpace(archive.CameraId))
        return BadRequest(new { code = "INVALID_PROBE", message = "archive.cameraId must not be empty" });
      if (!scope.All && !scope.Contains(archive.CameraId))
        return StatusCode(StatusCodes.Status403Forbidden);

      var probe = new ArchiveTrackProbe
      {
        CameraId = archive.CameraId,
        TrackId = archive.TrackId,
        Ts = Timestamp.FromDateTime(archive.Ts.ToUniversalTime()),
      };
      if (!string.IsNullOrWhiteSpace(archive.Kind))
      {
        if (!TryParseKind(archive.Kind, out var parsedKind))
          return BadRequest(new { code = "INVALID_KIND", message = $"unsupported kind '{archive.Kind}'" });
        probe.Kind = parsedKind;
      }
      request.ArchiveProbe = probe;
    }
    else
    {
      var image = dto.Image!;
      if (!TryParseKind(image.Kind, out var parsedKind))
        return BadRequest(new { code = "INVALID_KIND", message = $"unsupported kind '{image.Kind}'" });
      if (string.IsNullOrWhiteSpace(image.JpegBase64))
        return BadRequest(new { code = "INVALID_PROBE", message = "image.jpegBase64 must not be empty" });
      byte[] bytes;
      try
      {
        bytes = Convert.FromBase64String(image.JpegBase64);
      }
      catch (FormatException)
      {
        return BadRequest(new { code = "INVALID_PROBE", message = "image.jpegBase64 must be valid base64" });
      }

      var probe = new ImageProbe
      {
        Jpeg = Google.Protobuf.ByteString.CopyFrom(bytes),
        Kind = parsedKind,
        DetectFirst = image.DetectFirst,
      };
      if (image.Bbox is not null)
      {
        probe.Bbox = new BoundingBox
        {
          X = image.Bbox.X,
          Y = image.Bbox.Y,
          Width = image.Bbox.Width,
          Height = image.Bbox.Height,
        };
      }
      request.ImageProbe = probe;
    }

    try
    {
      var response = await _analyticsClient.SearchSimilarDetectionsAsync(request, cancellationToken: ct);
      // Detections that were not compared because their vectors came from another embedding model
      // (an archive recorded before the worker's re-id model changed). A header, so the body stays
      // the plain hit list it has always been.
      Response.Headers["X-Other-Model-Candidates"] = response.OtherModelCandidates.ToString(System.Globalization.CultureInfo.InvariantCulture);
      return Ok(response.Hits.Select(h => new SimilarDetectionHitDto
      {
        Detection = new AnalyticsDetectionDto
        {
          Id = h.Detection.Id,
          EventId = h.Detection.EventId,
          EventUuid = h.Detection.EventUuid,
          ObjectId = h.Detection.HasObjectId ? h.Detection.ObjectId : null,
          CameraId = h.Detection.CameraId,
          TrackId = h.Detection.TrackId,
          Kind = AnalyticsKindMapper.ToStringId(h.Detection.Kind),
          Confidence = h.Detection.Confidence,
          DetectedAt = h.Detection.DetectedAt.ToDateTime(),
          PlateText = h.Detection.HasPlateText ? h.Detection.PlateText : null,
          HasCrop = h.Detection.HasCrop,
        },
        Score = h.Score,
      }));
    }
    catch (Grpc.Core.RpcException ex) when (ex.StatusCode == Grpc.Core.StatusCode.FailedPrecondition
                                            && ex.Status.Detail == "probe has no embedding")
    {
      return StatusCode(StatusCodes.Status422UnprocessableEntity,
        new {
          code = "PROBE_NO_EMBEDDING",
          message = "This object has no stored appearance vector. Face/vehicle search needs the matching embedder model on the analytics worker; person works with person_embedder. Restart the worker after adding the model, then search again on a freshly seen object.",
        });
    }
    catch (Grpc.Core.RpcException ex) when (ex.StatusCode == Grpc.Core.StatusCode.FailedPrecondition
                                            && ex.Status.Detail == "too many candidates")
    {
      return StatusCode(StatusCodes.Status422UnprocessableEntity,
        new {
          code = "TOO_MANY_CANDIDATES",
          message = "This time range has too many stored embeddings to search at once. Narrow the period.",
        });
    }
    catch (Grpc.Core.RpcException ex) when (ex.StatusCode == Grpc.Core.StatusCode.FailedPrecondition
                                            && ex.Status.Detail == "kind space unavailable")
    {
      return StatusCode(StatusCodes.Status422UnprocessableEntity,
        new { code = "KIND_SPACE_UNAVAILABLE", message = "Embedding space for selected kind is unavailable (worker embedder model missing or failed)." });
    }
    catch (Grpc.Core.RpcException ex) when (ex.StatusCode == Grpc.Core.StatusCode.NotFound)
    {
      return NotFound(new { code = "PROBE_NOT_FOUND", message = "Selected probe was not found." });
    }
    catch (Grpc.Core.RpcException ex) when (ex.StatusCode == Grpc.Core.StatusCode.Unavailable
                                            && ex.Status.Detail == "photo search unavailable")
    {
      return StatusCode(StatusCodes.Status503ServiceUnavailable,
        new { code = "PHOTO_SEARCH_UNAVAILABLE", message = "Photo search is temporarily unavailable." });
    }
  }

  private static bool TryParseKind(string raw, out DetectionKind kind)
  {
    kind = raw.Trim().ToLowerInvariant() switch
    {
      "person" => DetectionKind.Person,
      "face" => DetectionKind.Face,
      "vehicle" => DetectionKind.Vehicle,
      "license_plate" => DetectionKind.LicensePlate,
      "animal" => DetectionKind.Animal,
      _ => DetectionKind.Unspecified,
    };
    return kind != DetectionKind.Unspecified;
  }

}
