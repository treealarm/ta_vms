using System.Globalization;
using Microsoft.AspNetCore.Mvc;

namespace web_vms;

[ApiController]
[Route("api/[controller]")]
public partial class ArchController(
  IHlsArchiveService hlsService,
  IArchiveTsStreamer tsStreamer) : ControllerBase
{

  [HttpGet("hls/preview.m3u8")]
  public async Task<IActionResult> PreviewHlsPlaylist(
    [FromQuery] string cameraId,
    [FromQuery] string from,
    [FromQuery] string to,
    CancellationToken ct)
  {
    cameraId = cameraId?.Trim() ?? "";
    from = from?.Trim() ?? "";
    to = to?.Trim() ?? "";
    if (cameraId.Length == 0 || from.Length == 0 || to.Length == 0)
      return BadRequest(new { error = "cameraId, from, and to are required" });

    if (!DateTime.TryParse(from, null, DateTimeStyles.RoundtripKind, out var fromDt)
        || !DateTime.TryParse(to, null, DateTimeStyles.RoundtripKind, out var toDt))
      return BadRequest(new { error = "from and to must be parseable date/time" });

    fromDt = ToUtc(fromDt);
    toDt = ToUtc(toDt);
    if (fromDt >= toDt)
      return BadRequest(new { error = "from must be before to" });

    var result = await hlsService.BuildPreviewPlaylistAsync(cameraId, new TimelineSlice(fromDt, toDt), ct);
    return PlaylistResult(result);
  }

  [HttpGet("hls/from-files.m3u8")]
  public async Task<IActionResult> FromFilesHlsPlaylist(
    [FromQuery] string cameraId,
    [FromQuery] string startClipUid,
    [FromQuery] uint count = 1,
    CancellationToken ct = default)
  {
    cameraId = cameraId?.Trim() ?? "";
    var clipUid = startClipUid?.Trim() ?? "";
    if (cameraId.Length == 0 || clipUid.Length == 0)
      return BadRequest(new { error = "cameraId and startClipUid are required" });

    if (!Guid.TryParse(clipUid, out _))
      return BadRequest(new { error = "startClipUid must be a valid UUID" });

    var result = await hlsService.BuildFilesPlaylistAsync(cameraId, clipUid, count, ct);
    return PlaylistResult(result);
  }

  [HttpGet("hls/event.m3u8")]
  public async Task<IActionResult> EventHlsPlaylist(
    [FromQuery] string eventId,
    [FromQuery] string streamName,
    CancellationToken ct)
  {
    eventId    = eventId?.Trim()    ?? "";
    streamName = streamName?.Trim() ?? "";
    if (eventId.Length == 0 || streamName.Length == 0)
      return BadRequest(new { error = "eventId and streamName are required" });

    var result = await hlsService.BuildEventPlaylistAsync(eventId, streamName, ct);
    return PlaylistResult(result);
  }

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
