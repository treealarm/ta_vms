using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Vms.Fs;
using web_vms.Authorization;
using web_vms.Types.Dto;

namespace web_vms.Server.Controllers;

/// <summary>
/// Intervals during which a camera's source was unavailable — the archive's own record of why it
/// has holes.
/// </summary>
/// <remarks>
/// Read-only for now: filling a gap from the camera's SD card is still the manual import on the
/// Edge Archive tab. The lifecycle actions (refill / cancel / resume / ignore) arrive with the
/// scheduler.
/// </remarks>
[Route("api/gaps")]
[ApiController]
[Authorize]
public sealed class ArchiveGapsController(
  ICameraManager manager,
  ServerPairRegistry registry,
  ICameraAccessService access) : ControllerBase
{
  private static DateTime ToUtc(DateTime dt) => dt.Kind switch
  {
    DateTimeKind.Utc => dt,
    DateTimeKind.Local => dt.ToUniversalTime(),
    _ => DateTime.SpecifyKind(dt, DateTimeKind.Utc)
  };

  private static SourceOutageDto ToDto(SourceOutageRow r) => new()
  {
    OutageUid = r.OutageUid,
    CameraId = r.CameraId,
    StreamName = string.IsNullOrEmpty(r.StreamName) ? null : r.StreamName,
    StartedTs = r.StartedTs.ToDateTime(),
    EndedTs = r.EndedTs?.ToDateTime(),
    Cause = r.Cause,
    RecordingExpected = r.RecordingExpected,
    State = r.State,
    FilledMs = r.FilledMs,
    Attempts = r.Attempts,
    Message = r.Message
  };

  // A named camera lives on exactly one FS instance — ask it directly. Without one the query has to
  // fan out, same as retention events.
  private async Task<List<ListSourceOutagesResponse>> QueryAsync(
    string? cameraId, ListSourceOutagesRequest req, CancellationToken ct)
  {
    if (!string.IsNullOrWhiteSpace(cameraId))
    {
      var cam = await manager.Get(cameraId, ct);
      if (cam?.ServerId is not { } serverId)
        return [];
      var client = registry.GetFsClient(serverId);
      return [await client.ListSourceOutagesAsync(req, cancellationToken: ct)];
    }

    var results = await registry.FanOutFsAsync(
      (c, o) => c.ListSourceOutagesAsync(req, o).ResponseAsync, ct);
    return results.Select(r => r.Value).ToList();
  }

  /// <summary>Outages overlapping the window, newest first.</summary>
  [CameraAccess(CameraAction.ViewArchive, Source = CameraIdSource.Action)]
  [HttpGet]
  public async Task<IActionResult> List(
    [FromQuery] string? cameraId,
    [FromQuery] string? from,
    [FromQuery] string? to,
    [FromQuery] int? limit,
    // Camera health is the whole picture; the archive-gap view wants only the intervals that
    // actually cost recordings.
    [FromQuery] bool? recordingOnly,
    CancellationToken ct)
  {
    var scope = await access.ScopeAsync(User, CameraAction.ViewArchive, ct);
    if (!string.IsNullOrWhiteSpace(cameraId) && !scope.Contains(cameraId))
      return StatusCode(StatusCodes.Status403Forbidden);

    var req = new ListSourceOutagesRequest { Limit = (uint)Math.Clamp(limit ?? 200, 1, 1000) };
    if (!string.IsNullOrWhiteSpace(cameraId))
      req.CameraId = cameraId;
    if (!string.IsNullOrWhiteSpace(from))
    {
      if (!DateTime.TryParse(from, null, System.Globalization.DateTimeStyles.RoundtripKind, out var f))
        return BadRequest(new { error = "from must be RFC3339" });
      req.FromTs = Timestamp.FromDateTime(ToUtc(f));
    }
    if (!string.IsNullOrWhiteSpace(to))
    {
      if (!DateTime.TryParse(to, null, System.Globalization.DateTimeStyles.RoundtripKind, out var t))
        return BadRequest(new { error = "to must be RFC3339" });
      req.ToTs = Timestamp.FromDateTime(ToUtc(t));
    }

    var responses = await QueryAsync(cameraId, req, ct);
    var outages = responses
      .SelectMany(r => r.Outages)
      // Filtered after the query rather than inside VmsFs, so an instance never has to know which
      // cameras this particular caller may see.
      .Where(o => scope.Contains(o.CameraId))
      .Where(o => recordingOnly != true || o.RecordingExpected)
      .Select(ToDto)
      .OrderByDescending(o => o.StartedTs)
      .ToList();

    return Ok(outages);
  }
}
