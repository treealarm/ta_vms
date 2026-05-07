using System.Globalization;
using System.Text;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Vms.Fs;
using FsRecording = Vms.Fs.Recording;

namespace web_vms;

[ApiController]
[Route("api/[controller]")]
public partial class ArchController(Vms.Fs.FsService.FsServiceClient fsClient) : ControllerBase
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
      Response.StatusCode = 400;
      await Response.WriteAsJsonAsync(new { error = "cameraId, from, and to are required" }, ct);
      return;
    }

    if (!DateTime.TryParse(from, null, DateTimeStyles.RoundtripKind, out var fromDt)
        || !DateTime.TryParse(to, null, DateTimeStyles.RoundtripKind, out var toDt))
    {
      Response.StatusCode = 400;
      await Response.WriteAsJsonAsync(new { error = "from and to must be parseable date/time" }, ct);
      return;
    }

    fromDt = ToUtc(fromDt);
    toDt = ToUtc(toDt);
    if (fromDt >= toDt)
    {
      Response.StatusCode = 400;
      await Response.WriteAsJsonAsync(new { error = "from must be before to" }, ct);
      return;
    }

    var fsResp = await fsClient.GetRecordingsAsync(new GetRecordingsFromToRequest
    {
      CameraId = cameraId,
      FromTs = Timestamp.FromDateTime(fromDt),
      ToTs = Timestamp.FromDateTime(toDt),
      Limit = 500
    }, cancellationToken: ct);

    var kept = fsResp.Recordings
      .Where(r => !string.IsNullOrWhiteSpace(r.FilePath))
      .OrderBy(r => r.StartTs.ToDateTime())
      .ToList();

    if (kept.Count == 0)
    {
      Response.StatusCode = 404;
      await Response.WriteAsJsonAsync(new { error = "no recordings in range" }, ct);
      return;
    }

    var pieces = new List<HlsPiece>();
    foreach (var rec in kept)
    {
      var fp = rec.FilePath.Trim();
      StatArchiveTsResponse stat;
      try
      {
        stat = await fsClient.StatArchiveTsAsync(new StatArchiveTsRequest { RelativePath = fp },
          cancellationToken: ct);
      }
      catch (RpcException)
      {
        Response.StatusCode = 500;
        await Response.WriteAsJsonAsync(new { error = "stat archive failed" }, ct);
        return;
      }

      if (!stat.Exists)
        continue;

      pieces.AddRange(await BuildArchiveHlsSubsegmentsAsync(rec, fromDt, toDt, cameraId, ct));
    }

    if (pieces.Count == 0)
    {
      Response.StatusCode = 404;
      await Response.WriteAsJsonAsync(new { error = "no recordings in range" }, ct);
      return;
    }

    var maxSec = pieces.Max(p => p.DurSec);
    var targetDur = (int)Math.Ceiling(Math.Max(1, maxSec));
    var startOffset = HlsVodStartTimeOffset(pieces, fromDt);
    var playlistDur = pieces.Sum(p => p.DurSec);
    if (playlistDur > 0 && startOffset >= playlistDur)
      startOffset = playlistDur - 0.001;
    if (startOffset < 0 || double.IsNaN(startOffset))
      startOffset = 0;

    var b = new StringBuilder();
    b.Append("#EXTM3U\n");
    b.Append("#EXT-X-VERSION:6\n");
    b.Append($"#EXT-X-TARGETDURATION:{targetDur}\n");
    b.Append("#EXT-X-MEDIA-SEQUENCE:0\n");
    b.Append($"#EXT-X-START:TIME-OFFSET={startOffset:F3}\n");

    var prevClip = "";
    for (var i = 0; i < pieces.Count; i++)
    {
      var p = pieces[i];
      if (i > 0 && p.ClipPath != prevClip)
        b.Append("#EXT-X-DISCONTINUITY\n");
      prevClip = p.ClipPath;

      var q = new Dictionary<string, string>
      {
        ["cameraId"] = cameraId,
        ["path"] = p.ClipPath
      };
      if (p.Offset > 0)
        q["offset"] = p.Offset.ToString();
      if (p.Length > 0)
        q["length"] = p.Length.ToString();

      var qs = string.Join("&", q.Select(kv =>
        $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
      var segmentUri = $"/api/arch/hls/ts?{qs}";
      b.Append($"#EXTINF:{p.DurSec:F3},\n");
      b.Append(segmentUri);
      b.Append('\n');
    }

    b.Append("#EXT-X-ENDLIST\n");

    Response.ContentType = "application/vnd.apple.mpegurl";
    Response.Headers.CacheControl = "private, max-age=60";
    await Response.WriteAsync(b.ToString(), ct);
  }

  [HttpGet("hls/from-files.m3u8")]
  public async Task FromFilesHlsPlaylist(
    [FromQuery] string cameraId,
    [FromQuery] string clipUid,
    [FromQuery] uint count = 1,
    CancellationToken ct = default)
  {
    cameraId = cameraId?.Trim() ?? "";
    clipUid = clipUid?.Trim() ?? "";
    if (cameraId.Length == 0 || clipUid.Length == 0)
    {
      Response.StatusCode = 400;
      await Response.WriteAsJsonAsync(new { error = "cameraId and clipUid are required" }, ct);
      return;
    }

    if (!Guid.TryParse(clipUid, out _))
    {
      Response.StatusCode = 400;
      await Response.WriteAsJsonAsync(new { error = "clipUid must be a valid UUID" }, ct);
      return;
    }

    GetRecordingsResponse clipsResp;
    try
    {
      clipsResp = await fsClient.GetClipsFromAsync(new GetClipsFromRequest
      {
        CameraId = cameraId,
        ClipUid = clipUid,
        Count = count == 0 ? 1 : count
      }, cancellationToken: ct);
    }
    catch (RpcException ex)
    {
      Response.StatusCode = 500;
      await Response.WriteAsJsonAsync(new { error = ex.Status.Detail }, ct);
      return;
    }

    if (clipsResp.Recordings.Count == 0)
    {
      Response.StatusCode = 404;
      await Response.WriteAsJsonAsync(new { error = "no clips found" }, ct);
      return;
    }

    var pieces = new List<HlsPiece>();
    foreach (var rec in clipsResp.Recordings)
    {
      var segWall = rec.StartTs.ToDateTime();
      var durSec = await RecordingWallDurationSec(cameraId, rec, segWall, ct);
      var durMs = (long)(durSec * 1000);
      if (durMs <= 0)
        durMs = 1;
      pieces.Add(new HlsPiece(rec.FilePath, 0, 0, durSec, 0, durMs, segWall));
    }

    var maxSec = pieces.Max(p => p.DurSec);
    var targetDur = (int)Math.Ceiling(Math.Max(1, maxSec));

    var b = new StringBuilder();
    b.Append("#EXTM3U\n");
    b.Append("#EXT-X-VERSION:6\n");
    b.Append($"#EXT-X-TARGETDURATION:{targetDur}\n");
    b.Append("#EXT-X-MEDIA-SEQUENCE:0\n");

    var prevClip = "";
    for (var i = 0; i < pieces.Count; i++)
    {
      var p = pieces[i];
      if (i > 0 && p.ClipPath != prevClip)
        b.Append("#EXT-X-DISCONTINUITY\n");
      prevClip = p.ClipPath;

      var qs = $"cameraId={Uri.EscapeDataString(cameraId)}&path={Uri.EscapeDataString(p.ClipPath)}";
      b.Append($"#EXTINF:{p.DurSec:F3},\n");
      b.Append($"/api/arch/hls/ts?{qs}\n");
    }

    b.Append("#EXT-X-ENDLIST\n");

    Response.ContentType = "application/vnd.apple.mpegurl";
    Response.Headers.CacheControl = "private, max-age=60";
    await Response.WriteAsync(b.ToString(), ct);
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
      Response.StatusCode = 400;
      await Response.WriteAsJsonAsync(new { error = "cameraId and path are required" }, ct);
      return;
    }

    StatArchiveTsResponse stat;
    try
    {
      stat = await fsClient.StatArchiveTsAsync(new StatArchiveTsRequest { RelativePath = path },
        cancellationToken: ct);
    }
    catch (RpcException ex)
    {
      Response.StatusCode = 500;
      await Response.WriteAsJsonAsync(new { error = ex.Status.Detail }, ct);
      return;
    }

    if (!stat.Exists)
    {
      Response.StatusCode = 404;
      await Response.WriteAsJsonAsync(new { error = string.IsNullOrWhiteSpace(stat.Message) ? "not found" : stat.Message }, ct);
      return;
    }

    var fileSize = stat.SizeBytes;
    if (offset > fileSize)
    {
      Response.StatusCode = 400;
      await Response.WriteAsJsonAsync(new { error = "offset past end of file" }, ct);
      return;
    }

    var maxFromBase = fileSize - offset;
    var entityLen = maxFromBase;
    if (length > 0)
      entityLen = Math.Min(length, maxFromBase);

    var rangeHdr = Request.Headers["Range"].ToString();
    var (rngStart, rngLen, hasRange) = ParseHttpRange(rangeHdr, entityLen);

    var req = new ReadArchiveTsRequest { RelativePath = path };
    if (hasRange)
    {
      req.Offset = offset + rngStart;
      req.Length = rngLen;
    }
    else
    {
      req.Offset = offset;
      if (length > 0)
        req.Length = entityLen;
    }

    Response.Headers.AcceptRanges = "bytes";
    Response.ContentType = ArchiveTsContentType(path);

    if (Request.Method == HttpMethods.Head)
    {
      if (hasRange)
      {
        var end = rngStart + rngLen - 1;
        Response.Headers.ContentRange = $"bytes {rngStart}-{end}/{entityLen}";
        Response.ContentLength = (long)rngLen;
        Response.StatusCode = StatusCodes.Status206PartialContent;
      }
      else
      {
        Response.ContentLength = (long)entityLen;
        Response.StatusCode = 200;
      }

      return;
    }

    AsyncServerStreamingCall<ReadArchiveTsChunk>? call = null;
    try
    {
      call = fsClient.ReadArchiveTs(req, cancellationToken: ct);
    }
    catch (RpcException ex) when (ex.StatusCode == Grpc.Core.StatusCode.NotFound)
    {
      Response.StatusCode = 404;
      await Response.WriteAsJsonAsync(new { error = ex.Status.Detail }, ct);
      return;
    }
    catch (RpcException ex)
    {
      Response.StatusCode = 500;
      await Response.WriteAsJsonAsync(new { error = ex.Status.Detail }, ct);
      return;
    }

    if (hasRange)
    {
      var end = rngStart + rngLen - 1;
      Response.Headers.ContentRange = $"bytes {rngStart}-{end}/{entityLen}";
      Response.ContentLength = (long)rngLen;
      Response.StatusCode = StatusCodes.Status206PartialContent;
    }
    else
    {
      Response.ContentLength = (long)entityLen;
      Response.StatusCode = 200;
    }

    var stream = call.ResponseStream;
    while (await stream.MoveNext(ct))
    {
      var chunk = stream.Current;
      if (chunk.Data.Length == 0)
        continue;
      await Response.Body.WriteAsync(chunk.Data.Memory, ct);
    }
  }

  private static DateTime ToUtc(DateTime dt) =>
    dt.Kind switch
    {
      DateTimeKind.Utc => dt,
      DateTimeKind.Local => dt.ToUniversalTime(),
      _ => DateTime.SpecifyKind(dt, DateTimeKind.Utc)
    };

  private static string ArchiveTsContentType(string filePath) =>
    filePath.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase) ? "video/x-matroska" : "video/mp2t";



  private async Task<double> RecordingWallDurationSec(string cameraId, FsRecording rec, DateTime refWall,
    CancellationToken ct)
  {
    if (rec.EndTs != null)
    {
      var d = rec.EndTs.ToDateTime() - rec.StartTs.ToDateTime();
      var sec = d.TotalSeconds;
      if (sec > 0 && !double.IsNaN(sec))
        return sec;
    }

    var d2 = await ClipWallDuration(cameraId, rec.FilePath, refWall, ct);
    var sec2 = d2.TotalSeconds;
    if (sec2 <= 0 || double.IsNaN(sec2))
      return 3600;
    return sec2;
  }

  private async Task<TimeSpan> ClipWallDuration(string cameraId, string filePath, DateTime refWall,
    CancellationToken ct)
  {
    try
    {
      var recs = await fsClient.GetRecordingsAroundAsync(new GetRecordingsAroundRequest
      {
        CameraId = cameraId,
        Timestamp = Timestamp.FromDateTime(ToUtc(refWall)),
        Limit = 120
      }, cancellationToken: ct);

      foreach (var r in recs.Recordings)
      {
        if (r.FilePath != filePath)
          continue;
        if (r.EndTs != null)
          return r.EndTs.ToDateTime() - r.StartTs.ToDateTime();
        break;
      }
    }
    catch
    {
      // fallthrough
    }

    return TimeSpan.FromHours(1);
  }

  private async Task<List<HlsPiece>> BuildArchiveHlsSubsegmentsAsync(
    FsRecording rec,
    DateTime wallFrom,
    DateTime wallTo,
    string cameraId,
    CancellationToken ct)
  {
    var segWall = rec.StartTs.ToDateTime();
    var refT = rec.StartTs.ToDateTime() > wallFrom ? rec.StartTs.ToDateTime() : wallFrom;
    var durSecTotal = await RecordingWallDurationSec(cameraId, rec, refT, ct);
    var durMs = (long)(durSecTotal * 1000);
    if (durMs <= 0)
      durMs = 1;
    var localToMs = (long)(wallTo - segWall).TotalMilliseconds;
    if (localToMs <= 0)
      return [];
    return [new HlsPiece(rec.FilePath, 0, 0, durSecTotal, 0, durMs, segWall)];
  }

  private static double HlsVodStartTimeOffset(List<HlsPiece> pieces, DateTime fromWall)
  {
    var acc = 0.0;
    foreach (var p in pieces)
    {
      var w0 = p.SegWall.AddMilliseconds(p.TStartMs);
      var w1 = p.SegWall.AddMilliseconds(p.TEndMs);
      if (fromWall < w0)
        return acc;
      if (fromWall <= w1)
        return acc + (fromWall - w0).TotalSeconds;
      acc += p.DurSec;
    }

    return acc;
  }

  private static (ulong start, ulong length, bool hasRange) ParseHttpRange(string? hdr, ulong entityLen)
  {
    hdr = hdr?.Trim() ?? "";
    if (hdr.Length == 0 || entityLen == 0)
      return (0, 0, false);
    const string prefix = "bytes=";
    if (!hdr.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
      return (0, 0, false);

    var s = hdr.AsSpan(prefix.Length).Trim();
    var comma = s.IndexOf(',');
    if (comma >= 0)
      s = s[..comma].Trim();

    var dash = s.IndexOf('-');
    if (dash < 0)
      return (0, 0, false);

    var startStr = s[..dash].Trim();
    var endStr = s[(dash + 1)..].Trim();
    if (startStr.Length == 0)
      return (0, 0, false);

    if (!ulong.TryParse(startStr, out var start) || start >= entityLen)
      return (0, 0, false);

    ulong rngLen;
    if (endStr.Length == 0)
      rngLen = entityLen - start;
    else
    {
      if (!ulong.TryParse(endStr, out var end) || end < start || end >= entityLen)
        return (0, 0, false);
      rngLen = end - start + 1;
    }

    return (start, rngLen, true);
  }

  private readonly record struct HlsPiece(
    string ClipPath,
    ulong Offset,
    ulong Length,
    double DurSec,
    long TStartMs,
    long TEndMs,
    DateTime SegWall);
}
