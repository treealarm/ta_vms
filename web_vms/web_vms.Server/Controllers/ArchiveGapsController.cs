using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Vms.Fs;
using Vms.Onvif;
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
  ICameraAccessService access,
  OnvifRouter router,
  ILogger<ArchiveGapsController> logger) : ControllerBase
{
  // A search walks several SOAP round trips on the device, each with its own internal timeout.
  private static readonly TimeSpan SearchTimeout = TimeSpan.FromSeconds(60);

  /// <summary>
  /// Consecutive fruitless attempts before a gap stops being offered to automation. Reset by any
  /// progress at all, so a long gap recovered piece by piece never runs out precisely because it is
  /// succeeding.
  /// </summary>
  private const int MaxFruitlessAttempts = 3;

  /// <summary>
  /// Slivers shorter than this are not holes: clip boundaries land on keyframes while the recorded
  /// interval comes from a report, so the two never agree to the millisecond.
  /// </summary>
  private static readonly TimeSpan FillTolerance = TimeSpan.FromSeconds(1);

  /// <summary>
  /// How old a gap must be before an empty answer from the camera may be read as "gone for good".
  /// </summary>
  /// <remarks>
  /// The card is a ring buffer, so material only ever disappears — but that is true of old footage.
  /// A stretch recorded minutes ago can be missing from a search simply because the device has not
  /// finished indexing it, and calling that "exhausted" is irreversible: the gap would never be
  /// attempted again although the footage is right there.
  /// </remarks>
  private static readonly TimeSpan ExhaustAfter = TimeSpan.FromSeconds(
    int.TryParse(Environment.GetEnvironmentVariable("VMS_BACKFILL_EXHAUST_AFTER_SECONDS"), out var e) && e > 0
      ? e : 900);

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
      .Select(ToDto)
      .OrderByDescending(o => o.StartedTs)
      .ToList();

    return Ok(outages);
  }

  /// <summary>
  /// Re-reads what the archive holds for a gap and what the camera still has, and records the
  /// outcome on the row.
  /// </summary>
  /// <remarks>
  /// Called after a restore run. Without it every gap stays "not filled" forever, and — once
  /// automation exists — a gap whose footage the camera has already overwritten would be retried
  /// until the end of time. The judgement is deliberately made from the archive and the device
  /// rather than from "did the job report success": a job can succeed and still recover nothing.
  /// </remarks>
  [CameraAccess(CameraAction.Control, Source = CameraIdSource.Action)]
  [HttpPost("{outageUid}/refresh")]
  public async Task<IActionResult> Refresh(string outageUid, CancellationToken ct)
  {
    var (row, client) = await FindAsync(outageUid, ct);
    if (row is null || client is null)
      return NotFound(new { message = "gap not found" });
    if (!await access.IsAllowedAsync(User, row.CameraId, CameraAction.Control, ct))
      return Forbid();

    var startedTs = row.StartedTs.ToDateTime();
    var endedTs = row.EndedTs?.ToDateTime() ?? DateTime.UtcNow;
    if (endedTs <= startedTs)
      return Ok(ToDto(row));

    // What the archive actually holds inside the gap.
    var timeline = await client.GetRecordingTimelineAsync(new GetRecordingTimelineRequest
    {
      CameraIds = { row.CameraId },
      StreamName = row.StreamName ?? string.Empty,
      FromTs = Timestamp.FromDateTime(startedTs),
      ToTs = Timestamp.FromDateTime(endedTs),
      MinGapMs = 1000,
    }, cancellationToken: ct);

    // Only footage recovered from the camera counts: the live clips inside a gap are the ones the
    // failing source produced, and they are the reason the gap exists.
    var covered = timeline.Tracks
      .SelectMany(t => t.Intervals)
      .Where(i => i.Origin == "edge")
      .Select(i => (Start: i.StartTs.ToDateTime(), End: i.EndTs.ToDateTime()))
      .ToList();
    var missing = Complement(covered, startedTs, endedTs)
      .Where(m => m.End - m.Start >= FillTolerance)
      .ToList();
    var filledMs = (long)(endedTs - startedTs - missing.Aggregate(
      TimeSpan.Zero, (acc, m) => acc + (m.End - m.Start))).TotalMilliseconds;
    var progressed = filledMs > row.FilledMs;

    string state;
    var message = string.Empty;
    var attempts = row.Attempts;

    if (missing.Count == 0)
    {
      state = "filled";
    }
    else if (await StillOnCameraAsync(row.CameraId, missing, ct) is { } fillable
             && fillable.Count == 0
             && DateTime.UtcNow - endedTs >= ExhaustAfter
             // One empty answer can be the network rather than the card; it takes a repeat.
             && row.Attempts >= 1)
    {
      // Terminal, and safe to be once the gap is old enough: the card is a ring buffer, so footage
      // that is gone never returns. Said too early it would be a lie — see ExhaustAfter.
      state = "exhausted";
      message = "the camera no longer holds the missing period";
    }
    else
    {
      // Counts only fruitless tries: any recovered footage means the run is working, however slowly.
      attempts = progressed ? 0 : row.Attempts + 1;
      state = attempts >= MaxFruitlessAttempts ? "partial" : "open";
      if (state == "partial")
        message = "no progress after repeated attempts";
    }

    await client.UpdateSourceOutageAsync(new UpdateSourceOutageRequest
    {
      OutageUid = row.OutageUid,
      State = state,
      FilledMs = Math.Max(0, filledMs),
      Attempts = attempts,
      Message = message,
    }, cancellationToken: ct);

    var (updated, _) = await FindAsync(outageUid, ct);
    return Ok(updated is null ? ToDto(row) : ToDto(updated));
  }

  /// <summary>
  /// Marks a restore as under way, so the gap says so to anyone else looking at the tab.
  /// </summary>
  /// <remarks>
  /// Also what keeps a manual run and an automatic one from fighting over the same camera: whoever
  /// takes the gap first is visible, and the device would refuse the second job anyway.
  /// </remarks>
  [CameraAccess(CameraAction.Control, Source = CameraIdSource.Action)]
  [HttpPost("{outageUid}/filling")]
  public Task<IActionResult> MarkFilling(string outageUid, CancellationToken ct) =>
    SetStateAsync(outageUid, "filling", string.Empty, ct, keepAttempts: true);

  /// <summary>Takes a gap out of automation's reach until the operator hands it back.</summary>
  [CameraAccess(CameraAction.Control, Source = CameraIdSource.Action)]
  [HttpPost("{outageUid}/hold")]
  public Task<IActionResult> Hold(string outageUid, CancellationToken ct) =>
    SetStateAsync(outageUid, "held", "held by the operator", ct);

  /// <summary>Returns a gap the operator had taken out of automation — held or ignored alike.</summary>
  [CameraAccess(CameraAction.Control, Source = CameraIdSource.Action)]
  [HttpPost("{outageUid}/resume")]
  public Task<IActionResult> Resume(string outageUid, CancellationToken ct) =>
    SetStateAsync(outageUid, "open", string.Empty, ct);

  /// <summary>
  /// Declares a gap not worth recovering, so it stops being work.
  /// </summary>
  /// <remarks>
  /// A judgement about value, which nothing else in the system can make: planned maintenance, a
  /// camera switched off on purpose, footage nobody wants. Unlike <c>exhausted</c> — which says the
  /// card no longer holds the material and is a discovered fact — this is a decision, so
  /// <see cref="Resume"/> takes it back.
  /// </remarks>
  [CameraAccess(CameraAction.Control, Source = CameraIdSource.Action)]
  [HttpPost("{outageUid}/ignore")]
  public Task<IActionResult> Ignore(string outageUid, CancellationToken ct) =>
    SetStateAsync(outageUid, "ignored", "ignored by the operator", ct);

  /// <summary>
  /// Asks for the missing footage to be fetched now.
  /// </summary>
  /// <remarks>
  /// Deliberately does not pull anything itself: it hands the gap back to the scheduler and clears
  /// the wait, so operator and automation reach the camera down one path. Two paths would race for
  /// the device's single import slot and the loser is refused, which is a button that works or not
  /// depending on timing the operator cannot see; and work started straight from a request is a live
  /// call rather than a row, so a restart mid-pull loses it instead of resuming.
  ///
  /// The scheduler decides what is actually missing when it picks the gap up, subtracting what the
  /// archive already holds, so pressing this twice costs nothing.
  /// </remarks>
  [CameraAccess(CameraAction.Control, Source = CameraIdSource.Action)]
  [HttpPost("{outageUid}/refill")]
  public Task<IActionResult> Refill(string outageUid, CancellationToken ct) =>
    SetStateAsync(outageUid, "open", string.Empty, ct, retryNow: true);

  private async Task<IActionResult> SetStateAsync(
    string outageUid, string state, string message, CancellationToken ct,
    bool keepAttempts = false, bool retryNow = false)
  {
    var (row, client) = await FindAsync(outageUid, ct);
    if (row is null || client is null)
      return NotFound(new { message = "gap not found" });
    if (!await access.IsAllowedAsync(User, row.CameraId, CameraAction.Control, ct))
      return Forbid();

    await client.UpdateSourceOutageAsync(new UpdateSourceOutageRequest
    {
      OutageUid = row.OutageUid,
      State = state,
      FilledMs = row.FilledMs,
      // Cleared on hold and resume alike: the operator taking charge says nothing about whether the
      // previous automatic attempts were fruitless. A run that is merely starting has not earned a
      // reset yet — that is decided from what it recovers.
      Attempts = keepAttempts ? row.Attempts : 0,
      Message = message,
      RetryNow = retryNow,
    }, cancellationToken: ct);

    var (updated, _) = await FindAsync(outageUid, ct);
    return Ok(updated is null ? ToDto(row) : ToDto(updated));
  }

  // The row lives on exactly one FS instance and nothing but its uid is known, so this fans out.
  private async Task<(SourceOutageRow? Row, FsService.FsServiceClient? Client)> FindAsync(
    string outageUid, CancellationToken ct)
  {
    var req = new ListSourceOutagesRequest { OutageUid = outageUid, Limit = 1 };
    var results = await registry.FanOutFsAsync(
      (c, o) => c.ListSourceOutagesAsync(req, o).ResponseAsync, ct);
    foreach (var result in results)
    {
      var row = result.Value.Outages.FirstOrDefault();
      if (row is not null)
        return (row, result.Client);
    }
    return (null, null);
  }

  /// <summary>What of `missing` the camera still has, or null when it could not be asked.</summary>
  private async Task<List<(DateTime Start, DateTime End)>?> StillOnCameraAsync(
    string cameraId, List<(DateTime Start, DateTime End)> missing, CancellationToken ct)
  {
    if (missing.Count == 0)
      return [];

    try
    {
      var resp = await (await router.OnvifAsync(cameraId, ct)).ListEdgeRecordingIntervalsAsync(new EdgeIntervalsRequest
      {
        SourceId = cameraId,
        StartTime = missing[0].Start.ToString("O"),
        EndTime = missing[^1].End.ToString("O"),
        MaxResults = 200,
      }, new CallOptions(deadline: DateTime.UtcNow.Add(SearchTimeout), cancellationToken: ct));

      if (!resp.Searched)
        return null;

      // One interval per track (video, audio, metadata) describes the same stretch, so they are
      // collapsed before being compared with what is missing.
      var available = new List<(DateTime Start, DateTime End)>();
      foreach (var span in resp.Intervals
        .Select(i => (Start: DateTime.Parse(i.StartTime).ToUniversalTime(),
                      End: DateTime.Parse(i.EndTime).ToUniversalTime()))
        .Where(s => s.End > s.Start)
        .OrderBy(s => s.Start))
      {
        if (available.Count > 0 && span.Start <= available[^1].End)
        {
          if (span.End > available[^1].End)
            available[^1] = (available[^1].Start, span.End);
          continue;
        }
        available.Add(span);
      }

      return missing
        .SelectMany(m => available
          .Select(a => (Start: m.Start > a.Start ? m.Start : a.Start, End: m.End < a.End ? m.End : a.End))
          .Where(x => x.End > x.Start))
        .ToList();
    }
    catch (Exception ex)
    {
      // A camera that cannot be asked is not a camera that has nothing: never conclude "exhausted"
      // from a failed query, or one network hiccup would close a gap that is still recoverable.
      logger.LogWarning(ex, "Could not ask camera {CameraId} what it still holds", cameraId);
      return null;
    }
  }

  private static List<(DateTime Start, DateTime End)> Complement(
    List<(DateTime Start, DateTime End)> covered, DateTime from, DateTime to)
  {
    var result = new List<(DateTime, DateTime)>();
    var cursor = from;
    foreach (var (start, end) in covered.OrderBy(c => c.Start))
    {
      if (start > cursor)
        result.Add((cursor, start < to ? start : to));
      if (end > cursor)
        cursor = end;
      if (cursor >= to)
        break;
    }
    if (cursor < to)
      result.Add((cursor, to));
    return result;
  }
}
