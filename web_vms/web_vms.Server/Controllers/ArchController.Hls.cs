using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Vms.Fs;
using web_vms.Authorization;

namespace web_vms;

[ApiController]
[Route("api/[controller]")]
public partial class ArchController(
  IHlsArchiveService hlsService,
  IArchiveTsStreamer tsStreamer,
  ArchiveOwnershipCache ownership,
  ServerPairRegistry registry,
  ICameraAccessService access) : ControllerBase
{

  [CameraAccess(CameraAction.ViewArchive)]
  [HttpGet("hls/preview.m3u8")]
  public async Task<IActionResult> PreviewHlsPlaylist(
    [FromQuery] string cameraId,
    [FromQuery] string from,
    [FromQuery] string to,
    [FromQuery] string? streamName = null,
    CancellationToken ct = default)
  {
    cameraId = cameraId?.Trim() ?? "";
    from = from?.Trim() ?? "";
    to = to?.Trim() ?? "";
    streamName = streamName?.Trim();
    if (cameraId.Length == 0 || from.Length == 0 || to.Length == 0)
      return BadRequest(new { error = "cameraId, from, and to are required" });

    if (!DateTime.TryParse(from, null, DateTimeStyles.RoundtripKind, out var fromDt)
        || !DateTime.TryParse(to, null, DateTimeStyles.RoundtripKind, out var toDt))
      return BadRequest(new { error = "from and to must be parseable date/time" });

    fromDt = ToUtc(fromDt);
    toDt = ToUtc(toDt);
    if (fromDt >= toDt)
      return BadRequest(new { error = "from must be before to" });

    var result = await hlsService.BuildPreviewPlaylistAsync(cameraId, streamName, new TimelineSlice(fromDt, toDt), ct);
    return PlaylistResult(result);
  }

  [CameraAccess(CameraAction.ViewArchive)]
  [HttpGet("hls/from-files.m3u8")]
  public async Task<IActionResult> FromFilesHlsPlaylist(
    [FromQuery] string cameraId,
    [FromQuery] string startClipUid,
    [FromQuery] uint count = 1,
    [FromQuery] string? from = null,
    CancellationToken ct = default)
  {
    cameraId = cameraId?.Trim() ?? "";
    var clipUid = startClipUid?.Trim() ?? "";
    if (cameraId.Length == 0 || clipUid.Length == 0)
      return BadRequest(new { error = "cameraId and startClipUid are required" });

    if (!Guid.TryParse(clipUid, out _))
      return BadRequest(new { error = "startClipUid must be a valid UUID" });

    DateTime? fromDt = null;
    if (!string.IsNullOrWhiteSpace(from))
    {
      if (!DateTime.TryParse(from, null, DateTimeStyles.RoundtripKind, out var parsed))
        return BadRequest(new { error = "from must be a parseable date/time" });
      fromDt = ToUtc(parsed);
    }

    var result = await hlsService.BuildFilesPlaylistAsync(cameraId, clipUid, count, ct, fromDt);
    return PlaylistResult(result);
  }

  // Addressed by episode, so the camera has to be resolved from the clips first.
  [CameraAccess(CameraAction.ViewArchive, Source = CameraIdSource.Action)]
  [HttpGet("hls/event.m3u8")]
  public async Task<IActionResult> EventHlsPlaylist(
    [FromQuery] string eventUuid,
    [FromQuery] string? streamName,
    [FromQuery] string? from,
    [FromQuery] string? to,
    CancellationToken ct)
  {
    eventUuid  = eventUuid?.Trim() ?? "";
    streamName = streamName?.Trim();
    // Validate before fan-out so a malformed uuid can't cool down an FS instance (see FSController).
    if (!Guid.TryParse(eventUuid, out _))
      return BadRequest(new { error = "valid eventUuid is required" });

    if (!await MayViewEventAsync(eventUuid, ct))
      return NotFound(new { error = "event not found" });

    // Optional [from, to] restricts the source to one playback chunk of a long event (see
    // FSController.GetEventPlaylists). Absent => the whole event.
    DateTime? fromTs = DateTime.TryParse(from, null, System.Globalization.DateTimeStyles.RoundtripKind, out var f) ? f.ToUniversalTime() : null;
    DateTime? toTs   = DateTime.TryParse(to, null, System.Globalization.DateTimeStyles.RoundtripKind, out var tt) ? tt.ToUniversalTime() : null;

    var result = await hlsService.BuildEventPlaylistAsync(eventUuid, streamName, fromTs, toTs, ct);
    return PlaylistResult(result);
  }

  // Addressed by clip, and hit once per segment — the owning camera comes from the cache the
  // playlist build primed, with an RPC only on a cold miss.
  [CameraAccess(CameraAction.ViewArchive, Source = CameraIdSource.Action)]
  [HttpGet("hls/ts")]
  [HttpHead("hls/ts")]
  public async Task<IActionResult> ServeArchiveTs(
    [FromQuery] string clipUid,
    [FromQuery] ulong offset = 0,
    [FromQuery] ulong length = 0,
    CancellationToken ct = default)
  {
    clipUid = clipUid?.Trim() ?? "";
    if (clipUid.Length == 0)
      return BadRequest(new { error = "clipUid is required" });

    if (!Guid.TryParse(clipUid, out _))
      return BadRequest(new { error = "clipUid must be a valid UUID" });

    // A whole-file request (no offset/length, no Range) is a download, not playback — that is the
    // export permission. Playlist segments always carry a byte window.
    var isWholeFileDownload = offset == 0 && length == 0
      && string.IsNullOrEmpty(Request.Headers["Range"].ToString());
    var action = isWholeFileDownload ? CameraAction.Export : CameraAction.ViewArchive;

    var clipCamera = await ResolveClipCameraAsync(clipUid, ct);
    // Unknown clip and forbidden clip answer alike: a 403 would confirm the clip exists.
    if (clipCamera is null || !await access.IsAllowedAsync(User, clipCamera, action, ct))
      return NotFound(new { error = "clip not found" });

    var result = await tsStreamer.OpenAsync(clipUid, offset, length, Request.Headers["Range"].ToString(), ct);

    switch (result)
    {
      case ArchiveStreamResult.BadRequest br:
        return BadRequest(new { error = br.Reason });
      case ArchiveStreamResult.NotFound nf:
        return NotFound(new { error = nf.Reason });
      case ArchiveStreamResult.Error err:
        return StatusCode(StatusCodes.Status500InternalServerError, new { error = err.Reason });
      case ArchiveStreamResult.Ok ok:
        Response.Headers.AcceptRanges = "bytes";
        Response.ContentType = ok.ContentType;
        Response.ContentLength = ok.ContentLength;

        if (ok.Range is { } range)
        {
          Response.Headers.ContentRange = $"bytes {range.Start}-{range.Start + range.Length - 1}/{range.Total}";
          Response.StatusCode = StatusCodes.Status206PartialContent;
        }
        else
        {
          Response.StatusCode = StatusCodes.Status200OK;
        }

        if (!HttpMethods.IsHead(Request.Method))
        {
          await foreach (var chunk in ok.Data.WithCancellation(ct))
            await Response.Body.WriteAsync(chunk, ct);
        }

        return new EmptyResult();
    }

    return StatusCode(StatusCodes.Status500InternalServerError);
  }

  /// <summary>
  /// Clip -> camera. Served from the cache the playlist build primed; a miss (deep link, restart,
  /// eviction) costs one fan-out lookup, which then primes the cache for the rest of the playback.
  /// </summary>
  private async Task<string?> ResolveClipCameraAsync(string clipUid, CancellationToken ct)
  {
    if (ownership.TryGetClipCamera(clipUid, out var cached))
      return cached;

    var responses = await registry.FanOutFsAsync(
      (c, o) => c.GetRecordingByUidAsync(
        new GetRecordingByUidRequest { ClipUids = { clipUid } }, o).ResponseAsync, ct);

    var recording = responses
      .SelectMany(r => r.Value.Recordings)
      .FirstOrDefault(r => r.ClipUid == clipUid);

    if (recording is null || string.IsNullOrEmpty(recording.CameraId))
      return null;

    ownership.PrimeClips([recording]);
    return recording.CameraId;
  }

  private async Task<bool> MayViewEventAsync(string eventUuid, CancellationToken ct)
  {
    if (!ownership.TryGetEventCamera(eventUuid, out var cameraId))
    {
      var responses = await registry.FanOutFsAsync(
        (c, o) => c.GetClipsForEventAsync(new GetClipsForEventRequest { EventUuid = eventUuid }, o).ResponseAsync, ct);

      var recording = responses
        .SelectMany(r => r.Value.Recordings)
        .FirstOrDefault(r => !string.IsNullOrEmpty(r.CameraId));

      if (recording is null)
        return false;

      cameraId = recording.CameraId;
      ownership.PrimeEvent(eventUuid, cameraId);
    }

    return await access.IsAllowedAsync(User, cameraId, CameraAction.ViewArchive, ct);
  }

  private IActionResult PlaylistResult(HlsPlaylistResult result)
  {
    switch (result)
    {
      case HlsPlaylistResult.Ok ok:
        Response.Headers.CacheControl = "private, max-age=60";
        return Content(ok.Content, "application/vnd.apple.mpegurl");
      case HlsPlaylistResult.NotFound nf:
        return NotFound(new { error = nf.Reason });
      case HlsPlaylistResult.Error err:
        return StatusCode(StatusCodes.Status500InternalServerError, new { error = err.Reason });
    }

    return StatusCode(StatusCodes.Status500InternalServerError);
  }

  private static DateTime ToUtc(DateTime dt) =>
    dt.Kind switch
    {
      DateTimeKind.Utc => dt,
      DateTimeKind.Local => dt.ToUniversalTime(),
      _ => DateTime.SpecifyKind(dt, DateTimeKind.Utc)
    };
}
