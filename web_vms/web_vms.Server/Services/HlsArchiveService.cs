using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.AspNetCore.Http;
using Vms.Fs;
using FsRecording = Vms.Fs.Recording;

namespace web_vms;

public sealed class HlsArchiveService(FsService.FsServiceClient fsClient) : IHlsArchiveService
{
  public async Task<HlsPlaylistResult> BuildPreviewPlaylistAsync(
    string cameraId, DateTime fromUtc, DateTime toUtc, CancellationToken ct)
  {
    var fsResp = await fsClient.GetRecordingsAsync(new GetRecordingsFromToRequest
    {
      CameraId = cameraId,
      FromTs = Timestamp.FromDateTime(fromUtc),
      ToTs = Timestamp.FromDateTime(toUtc),
      Limit = 500
    }, cancellationToken: ct);

    var kept = fsResp.Recordings
      .Where(r => !string.IsNullOrWhiteSpace(r.FilePath))
      .OrderBy(r => r.StartTs.ToDateTime())
      .ToList();

    if (kept.Count == 0)
      return new HlsPlaylistResult.NotFound("no recordings in range");

    var pieces = new List<HlsPiece>();
    foreach (var rec in kept)
    {
      var fp = rec.FilePath.Trim();
      StatArchiveTsResponse stat;
      try
      {
        stat = await fsClient.StatArchiveTsAsync(
          new StatArchiveTsRequest { RelativePath = fp }, cancellationToken: ct);
      }
      catch (RpcException)
      {
        return new HlsPlaylistResult.Error("stat archive failed");
      }

      if (!stat.Exists)
        continue;

      pieces.AddRange(await BuildSubsegmentsAsync(rec, fromUtc, toUtc, cameraId, ct));
    }

    if (pieces.Count == 0)
      return new HlsPlaylistResult.NotFound("no recordings in range");

    var maxSec = pieces.Max(p => p.DurSec);
    var targetDur = (int)Math.Ceiling(Math.Max(1, maxSec));
    var startOffset = ComputeStartOffset(pieces, fromUtc);
    var playlistDur = pieces.Sum(p => p.DurSec);
    if (playlistDur > 0 && startOffset >= playlistDur)
      startOffset = playlistDur - 0.001;
    if (startOffset < 0 || double.IsNaN(startOffset))
      startOffset = 0;

    var playlist = new HlsPlaylistBuilder();
    playlist.AppendHeader(targetDur, startOffset);
    AppendSegments(playlist, pieces, cameraId, withOffsetAndLength: true);
    playlist.AppendEndList();

    return new HlsPlaylistResult.Ok(playlist.Build());
  }

  public async Task<HlsPlaylistResult> BuildFilesPlaylistAsync(
    string cameraId, string startClipUid, uint count, CancellationToken ct)
  {
    GetRecordingsResponse clipsResp;
    try
    {
      clipsResp = await fsClient.GetClipsFromAsync(new GetClipsFromRequest
      {
        CameraId = cameraId,
        StartClipUid = startClipUid,
        Count = count == 0 ? 1 : count
      }, cancellationToken: ct);
    }
    catch (RpcException ex)
    {
      return new HlsPlaylistResult.Error(ex.Status.Detail);
    }

    if (clipsResp.Recordings.Count == 0)
      return new HlsPlaylistResult.NotFound("no clips found");

    var pieces = new List<HlsPiece>();
    foreach (var rec in clipsResp.Recordings)
    {
      var segWall = rec.StartTs.ToDateTime();
      var durSec = await RecordingWallDurationSecAsync(cameraId, rec, segWall, ct);
      var durMs = (long)(durSec * 1000);
      if (durMs <= 0) durMs = 1;
      pieces.Add(new HlsPiece(rec.FilePath, 0, 0, durSec, 0, durMs, segWall));
    }

    var maxSec = pieces.Max(p => p.DurSec);
    var targetDur = (int)Math.Ceiling(Math.Max(1, maxSec));

    var playlist = new HlsPlaylistBuilder();
    playlist.AppendHeader(targetDur);
    AppendSegments(playlist, pieces, cameraId, withOffsetAndLength: false);
    playlist.AppendEndList();

    return new HlsPlaylistResult.Ok(playlist.Build());
  }

  private static void AppendSegments(
    HlsPlaylistBuilder playlist, List<HlsPiece> pieces, string cameraId, bool withOffsetAndLength)
  {
    var prevClip = "";
    for (var i = 0; i < pieces.Count; i++)
    {
      var p = pieces[i];
      if (i > 0 && p.ClipPath != prevClip)
        playlist.AppendDiscontinuity();
      prevClip = p.ClipPath;

      var qParams = new List<KeyValuePair<string, string?>>
      {
        new("cameraId", cameraId),
        new("path", p.ClipPath)
      };
      if (withOffsetAndLength)
      {
        if (p.Offset > 0) qParams.Add(new("offset", p.Offset.ToString()));
        if (p.Length > 0) qParams.Add(new("length", p.Length.ToString()));
      }

      playlist.AppendSegment("/api/arch/hls/ts" + QueryString.Create(qParams), p.DurSec);
    }
  }

  private async Task<double> RecordingWallDurationSecAsync(
    string cameraId, FsRecording rec, DateTime refWall, CancellationToken ct)
  {
    if (rec.EndTs != null)
    {
      var d = rec.EndTs.ToDateTime() - rec.StartTs.ToDateTime();
      var sec = d.TotalSeconds;
      if (sec > 0 && !double.IsNaN(sec))
        return sec;
    }

    var d2 = await ClipWallDurationAsync(cameraId, rec.FilePath, refWall, ct);
    var sec2 = d2.TotalSeconds;
    return sec2 <= 0 || double.IsNaN(sec2) ? 3600 : sec2;
  }

  private async Task<TimeSpan> ClipWallDurationAsync(
    string cameraId, string filePath, DateTime refWall, CancellationToken ct)
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
        if (r.FilePath != filePath) continue;
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

  private async Task<List<HlsPiece>> BuildSubsegmentsAsync(
    FsRecording rec, DateTime wallFrom, DateTime wallTo, string cameraId, CancellationToken ct)
  {
    var segWall = rec.StartTs.ToDateTime();
    var refT = segWall > wallFrom ? segWall : wallFrom;
    var durSecTotal = await RecordingWallDurationSecAsync(cameraId, rec, refT, ct);
    var durMs = (long)(durSecTotal * 1000);
    if (durMs <= 0) durMs = 1;
    var localToMs = (long)(wallTo - segWall).TotalMilliseconds;
    if (localToMs <= 0) return [];
    return [new HlsPiece(rec.FilePath, 0, 0, durSecTotal, 0, durMs, segWall)];
  }

  private static double ComputeStartOffset(List<HlsPiece> pieces, DateTime fromWall)
  {
    var acc = 0.0;
    foreach (var p in pieces)
    {
      var w0 = p.SegWall.AddMilliseconds(p.TStartMs);
      var w1 = p.SegWall.AddMilliseconds(p.TEndMs);
      if (fromWall < w0) return acc;
      if (fromWall <= w1) return acc + (fromWall - w0).TotalSeconds;
      acc += p.DurSec;
    }
    return acc;
  }

  private static DateTime ToUtc(DateTime dt) =>
    dt.Kind switch
    {
      DateTimeKind.Utc => dt,
      DateTimeKind.Local => dt.ToUniversalTime(),
      _ => DateTime.SpecifyKind(dt, DateTimeKind.Utc)
    };

  private readonly record struct HlsPiece(
    string ClipPath,
    ulong Offset,
    ulong Length,
    double DurSec,
    long TStartMs,
    long TEndMs,
    DateTime SegWall);
}
