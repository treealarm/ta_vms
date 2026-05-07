using System.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace web_vms;

[ApiController]
[Route("api/[controller]")]
public partial class ArchController(
  IHlsArchiveService hlsService,
  IArchiveTsStreamer tsStreamer) : ControllerBase
{

  [HttpGet("hls/preview.m3u8")]
  public async Task PreviewHlsPlaylist(
    [FromQuery] string cameraId,
    [FromQuery] string from,
    [FromQuery] string to,
    CancellationToken ct)
  {
    cameraId = cameraId?.Trim() ?? "";
    from = from?.Trim() ?? "";
    to = to?.Trim() ?? "";
    if (cameraId.Length == 0 || from.Length == 0 || to.Length == 0)
    {
      await Response.WriteErrorAsync(StatusCodes.Status400BadRequest, "cameraId, from, and to are required", ct);
      return;
    }

    if (!DateTime.TryParse(from, null, DateTimeStyles.RoundtripKind, out var fromDt)
        || !DateTime.TryParse(to, null, DateTimeStyles.RoundtripKind, out var toDt))
    {
      await Response.WriteErrorAsync(StatusCodes.Status400BadRequest, "from and to must be parseable date/time", ct);
      return;
    }

    fromDt = ToUtc(fromDt);
    toDt = ToUtc(toDt);
    if (fromDt >= toDt)
    {
      await Response.WriteErrorAsync(StatusCodes.Status400BadRequest, "from must be before to", ct);
      return;
    }

    var result = await hlsService.BuildPreviewPlaylistAsync(cameraId, fromDt, toDt, ct);
    await WritePlaylistResultAsync(result, ct);
  }

  [HttpGet("hls/from-files.m3u8")]
  public async Task FromFilesHlsPlaylist(
    [FromQuery] string cameraId,
    [FromQuery] string startClipUid,
    [FromQuery] uint count = 1,
    CancellationToken ct = default)
  {
    cameraId = cameraId?.Trim() ?? "";
    var clipUid = startClipUid?.Trim() ?? "";
    if (cameraId.Length == 0 || clipUid.Length == 0)
    {
      await Response.WriteErrorAsync(StatusCodes.Status400BadRequest, "cameraId and startClipUid are required", ct);
      return;
    }

    if (!Guid.TryParse(clipUid, out _))
    {
      await Response.WriteErrorAsync(StatusCodes.Status400BadRequest, "startClipUid must be a valid UUID", ct);
      return;
    }

    var result = await hlsService.BuildFilesPlaylistAsync(cameraId, clipUid, count, ct);
    await WritePlaylistResultAsync(result, ct);
  }

  [HttpGet("hls/ts")]
  public async Task ServeArchiveTs(
    [FromQuery] string cameraId,
    [FromQuery] string path,
    [FromQuery] ulong offset = 0,
    [FromQuery] ulong length = 0,
    CancellationToken ct = default)
  {
    if (Request.Method != HttpMethods.Get && Request.Method != HttpMethods.Head)
    {
      Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
      return;
    }

    cameraId = cameraId?.Trim() ?? "";
    path = path?.Trim() ?? "";
    if (cameraId.Length == 0 || path.Length == 0)
    {
      await Response.WriteErrorAsync(StatusCodes.Status400BadRequest, "cameraId and path are required", ct);
      return;
    }

    var rangeHdr = Request.Headers["Range"].ToString();
    var result = await tsStreamer.OpenAsync(path, offset, length, rangeHdr, ct);

    switch (result)
    {
      case ArchiveStreamResult.BadRequest br:
        await Response.WriteErrorAsync(StatusCodes.Status400BadRequest, br.Reason, ct);
        return;
      case ArchiveStreamResult.NotFound nf:
        await Response.WriteErrorAsync(StatusCodes.Status404NotFound, nf.Reason, ct);
        return;
      case ArchiveStreamResult.Error err:
        await Response.WriteErrorAsync(StatusCodes.Status500InternalServerError, err.Reason, ct);
        return;
      case ArchiveStreamResult.Ok ok:
        Response.Headers.AcceptRanges = "bytes";
        Response.ContentType = ok.ContentType;
        Response.ContentLength = ok.ContentLength;

        if (ok.Range is { } range)
        {
          var end = range.Start + range.Length - 1;
          Response.Headers.ContentRange = $"bytes {range.Start}-{end}/{range.Total}";
          Response.StatusCode = StatusCodes.Status206PartialContent;
        }
        else
        {
          Response.StatusCode = StatusCodes.Status200OK;
        }

        if (Request.Method == HttpMethods.Head)
          return;

        await foreach (var chunk in ok.Data.WithCancellation(ct))
          await Response.Body.WriteAsync(chunk, ct);
        break;
    }
  }

  private async Task WritePlaylistResultAsync(HlsPlaylistResult result, CancellationToken ct)
  {
    switch (result)
    {
      case HlsPlaylistResult.Ok ok:
        Response.ContentType = "application/vnd.apple.mpegurl";
        Response.Headers.CacheControl = "private, max-age=60";
        await Response.WriteAsync(ok.Content, ct);
        break;
      case HlsPlaylistResult.NotFound nf:
        await Response.WriteErrorAsync(StatusCodes.Status404NotFound, nf.Reason, ct);
        break;
      case HlsPlaylistResult.Error err:
        await Response.WriteErrorAsync(StatusCodes.Status500InternalServerError, err.Reason, ct);
        break;
    }
  }

  private static DateTime ToUtc(DateTime dt) =>
    dt.Kind switch
    {
      DateTimeKind.Utc => dt,
      DateTimeKind.Local => dt.ToUniversalTime(),
      _ => DateTime.SpecifyKind(dt, DateTimeKind.Utc)
    };
}
