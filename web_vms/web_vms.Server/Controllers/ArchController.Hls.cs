using System.Globalization;
using System.Text;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Vms.Fs;
using FsRecording = Vms.Fs.Recording;

namespace web_vms;

public partial class ArchController
{
  private const uint GapToleranceMs = 1000;
  private const int MaxHlsFromFilesPaths = 120;
  private static readonly TimeSpan ArchiveHlsSubsegmentTarget = TimeSpan.FromSeconds(4);

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

    var recs = fsResp.Recordings.OrderBy(r => r.StartTs.ToDateTime()).ToList();
    var kept = new List<FsRecording>();
    foreach (var rec in recs)
    {
      var fp = rec.FilePath?.Trim() ?? "";
      if (fp.Length == 0)
        continue;
      if (!await ValidateClipOwnedByCamera(cameraId, fp, ct))
        continue;
      kept.Add(rec);
    }

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

      GetArchiveTsClipIndexResponse? idxResp = null;
      try
      {
        idxResp = await fsClient.GetArchiveTsClipIndexAsync(
          new GetArchiveTsClipIndexRequest { RelativePath = fp }, cancellationToken: ct);
      }
      catch
      {
        // optional index
      }

      pieces.AddRange(await BuildArchiveHlsSubsegmentsAsync(rec, idxResp, stat.SizeBytes, fromDt, toDt, cameraId, ct));
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
  public async Task FromFilesHlsPlaylist([FromQuery] string cameraId, CancellationToken ct)
  {
    cameraId = cameraId?.Trim() ?? "";
    if (cameraId.Length == 0)
    {
      Response.StatusCode = 400;
      await Response.WriteAsJsonAsync(new { error = "cameraId is required" }, ct);
      return;
    }

    var raw = Request.Query["path"];
    var paths = new List<string>();
    var seen = new HashSet<string>(StringComparer.Ordinal);
    foreach (var p in raw)
    {
      var t = p?.Trim() ?? "";
      if (t.Length == 0 || seen.Contains(t))
        continue;
      seen.Add(t);
      paths.Add(t);
      if (paths.Count >= MaxHlsFromFilesPaths)
        break;
    }

    if (paths.Count == 0)
    {
      Response.StatusCode = 400;
      await Response.WriteAsJsonAsync(new { error = "at least one path parameter is required" }, ct);
      return;
    }

    var pieces = new List<HlsPiece>();

    foreach (var fp in paths)
    {
      if (!await ValidateClipOwnedByCamera(cameraId, fp, ct))
      {
        Response.StatusCode = 404;
        await Response.WriteAsJsonAsync(new { error = "clip not found" }, ct);
        return;
      }

      StatArchiveTsResponse stat;
      try
      {
        stat = await fsClient.StatArchiveTsAsync(new StatArchiveTsRequest { RelativePath = fp },
          cancellationToken: ct);
      }
      catch (RpcException)
      {
        Response.StatusCode = 500;
        await Response.WriteAsJsonAsync(new { error = "stat failed" }, ct);
        return;
      }

      if (!stat.Exists)
      {
        Response.StatusCode = 404;
        await Response.WriteAsJsonAsync(new { error = "segment not found on media server" }, ct);
        return;
      }

      RecordingRangeForClipResponse rr;
      try
      {
        rr = await fsClient.GetRecordingRangeForClipAsync(new GetRecordingRangeForClipRequest
        {
          CameraId = cameraId,
          FilePath = fp,
          GapToleranceMs = GapToleranceMs
        }, cancellationToken: ct);
      }
      catch (RpcException)
      {
        Response.StatusCode = 404;
        await Response.WriteAsJsonAsync(new { error = "clip metadata not found" }, ct);
        return;
      }

      if (!rr.Found || rr.ClipStartTs == null)
      {
        Response.StatusCode = 404;
        await Response.WriteAsJsonAsync(new { error = "clip metadata not found" }, ct);
        return;
      }

      var center = rr.ClipStartTs;
      var around = await fsClient.GetRecordingsAroundAsync(new GetRecordingsAroundRequest
      {
        CameraId = cameraId,
        Timestamp = center,
        Limit = 120
      }, cancellationToken: ct);

      FsRecording? rec = around.Recordings.FirstOrDefault(x => x.FilePath == fp);
      if (rec == null)
      {
        Response.StatusCode = 404;
        await Response.WriteAsJsonAsync(new { error = "recording row not found for clip" }, ct);
        return;
      }

      var segWall = rec.StartTs.ToDateTime();
      var durSec = await RecordingWallDurationSec(cameraId, rec, segWall, ct);
      var durMs = (long)(durSec * 1000);
      if (durMs <= 0)
        durMs = 1;

      pieces.Add(new HlsPiece(fp, 0, 0, durSec, 0, durMs, segWall));
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

      var qs =
        $"cameraId={Uri.EscapeDataString(cameraId)}&path={Uri.EscapeDataString(p.ClipPath)}";
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

    if (!await ValidateClipOwnedByCamera(cameraId, path, ct))
    {
      Response.StatusCode = 404;
      await Response.WriteAsJsonAsync(new { error = "clip not found" }, ct);
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

  private async Task<bool> ValidateClipOwnedByCamera(string cameraId, string filePath, CancellationToken ct)
  {
    try
    {
      var resp = await fsClient.GetRecordingRangeForClipAsync(new GetRecordingRangeForClipRequest
      {
        CameraId = cameraId,
        FilePath = filePath,
        GapToleranceMs = GapToleranceMs
      }, cancellationToken: ct);
      return resp.Found;
    }
    catch
    {
      return false;
    }
  }

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
    GetArchiveTsClipIndexResponse? idxResp,
    ulong fileSize,
    DateTime wallFrom,
    DateTime wallTo,
    string cameraId,
    CancellationToken ct)
  {
    var fp = rec.FilePath;
    TsClipIndex? idx = idxResp?.Index;
    var segWall = ClipSegmentWallUtc(idx, rec);
    if (segWall == default)
      return [];

    var refT = wallFrom;
    if (rec.StartTs.ToDateTime() > wallFrom)
      refT = rec.StartTs.ToDateTime();

    var durSecTotal = await RecordingWallDurationSec(cameraId, rec, refT, ct);
    var durMs = (long)(durSecTotal * 1000);
    if (durMs <= 0)
      durMs = 1;

    var localFromMs = (long)(wallFrom - segWall).TotalMilliseconds;
    if (localFromMs < 0)
      localFromMs = 0;
    var localToMs = (long)(wallTo - segWall).TotalMilliseconds;
    if (localToMs > durMs)
      localToMs = durMs;
    if (localToMs <= localFromMs)
      return [];

    var useIdx = idxResp is { Found: true } && idx != null && idx.Keyframes.Count > 0;
    if (!useIdx)
    {
      return
      [
        new HlsPiece(fp, 0, 0, durSecTotal, 0, durMs, segWall)
      ];
    }

    var kfs = idx!.Keyframes.OrderBy(k => k.TimeMsFromSegmentStart).ToList();

    var cur = KeyframeIdxAtOrBefore(kfs, localFromMs);
    var bucketMs = (long)ArchiveHlsSubsegmentTarget.TotalMilliseconds;

    var outPieces = new List<HlsPiece>();

    while (cur < kfs.Count)
    {
      var t0 = (long)kfs[cur].TimeMsFromSegmentStart;
      if (t0 >= localToMs)
        break;

      var o0 = kfs[cur].ByteOffset;
      if (o0 > fileSize)
        break;

      var bound = t0 + bucketMs;
      if (bound > localToMs)
        bound = localToMs;

      var endIdx = FirstKeyframeIdxAtOrAfter(kfs, cur + 1, bound);
      if (endIdx <= cur)
        endIdx = cur + 1;

      ulong o1;
      long t1;
      if (endIdx >= kfs.Count)
      {
        o1 = fileSize;
        t1 = durMs;
        if (t1 <= t0)
          t1 = t0 + 1;
      }
      else
      {
        o1 = kfs[endIdx].ByteOffset;
        t1 = (long)kfs[endIdx].TimeMsFromSegmentStart;
      }

      if (o1 <= o0)
      {
        cur = Math.Max(cur + 1, endIdx);
        continue;
      }

      if (o1 > fileSize)
        o1 = fileSize;

      var segLen = o1 - o0;
      var durSeg = (t1 - t0) / 1000.0;
      if (durSeg < 0.001)
        durSeg = 0.001;

      outPieces.Add(new HlsPiece(fp, o0, segLen, durSeg, t0, t1, segWall));
      cur = endIdx;
    }

    return outPieces;
  }

  private static DateTime ClipSegmentWallUtc(TsClipIndex? idx, FsRecording rec)
  {
    if (idx?.SegmentWallStart != null)
      return idx.SegmentWallStart.ToDateTime();
    return rec.StartTs.ToDateTime();
  }

  private static int KeyframeIdxAtOrBefore(IReadOnlyList<TsClipKeyframeEntry> kfs, long localMs)
  {
    if (kfs.Count == 0)
      return 0;
    var lo = 0;
    var hi = kfs.Count - 1;
    var ans = -1;
    while (lo <= hi)
    {
      var mid = (lo + hi) / 2;
      var t = (long)kfs[mid].TimeMsFromSegmentStart;
      if (t <= localMs)
      {
        ans = mid;
        lo = mid + 1;
      }
      else
        hi = mid - 1;
    }

    return ans < 0 ? 0 : ans;
  }

  private static int FirstKeyframeIdxAtOrAfter(IReadOnlyList<TsClipKeyframeEntry> kfs, int start, long boundMs)
  {
    for (var i = start; i < kfs.Count; i++)
    {
      if ((long)kfs[i].TimeMsFromSegmentStart >= boundMs)
        return i;
    }

    return kfs.Count;
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
