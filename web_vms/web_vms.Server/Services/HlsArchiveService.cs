using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.AspNetCore.Http;
using Vms.Fs;
using FsRecording = Vms.Fs.Recording;

namespace web_vms;

public sealed record ByteRange(ulong Offset, ulong Length);

public sealed record HlsSegment(
  string FilePath,
  ByteRange? ByteRange,
  DateTime WallStart,
  DateTime WallEnd)
{
  public TimeSpan Duration => WallEnd - WallStart;
}

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

    var segments = new List<HlsSegment>();
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

      var seg = await BuildRecordingSegmentAsync(rec, fromUtc, toUtc, cameraId, ct);
      if (seg is not null)
        segments.Add(seg);
    }

    if (segments.Count == 0)
      return new HlsPlaylistResult.NotFound("no recordings in range");

    var startOffset = ComputeStartOffset(segments, fromUtc);
    var playlistDur = segments.Sum(s => s.Duration.TotalSeconds);
    if (playlistDur > 0 && startOffset >= playlistDur)
      startOffset = playlistDur - 0.001;
    if (startOffset < 0 || double.IsNaN(startOffset))
      startOffset = 0;

    return new HlsPlaylistResult.Ok(BuildPlaylist(cameraId, segments, startOffset));
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

    var segments = new List<HlsSegment>();
    foreach (var rec in clipsResp.Recordings)
    {
      var wallStart = rec.StartTs.ToDateTime();
      var durSec = await RecordingWallDurationSecAsync(cameraId, rec, wallStart, ct);
      var dur = TimeSpan.FromSeconds(Math.Max(durSec, 0.001));
      segments.Add(new HlsSegment(rec.FilePath, null, wallStart, wallStart + dur));
    }

    return new HlsPlaylistResult.Ok(BuildPlaylist(cameraId, segments));
  }

  private static string BuildPlaylist(string cameraId, List<HlsSegment> segments, double? startOffset = null)
  {
    var targetDur = (int)Math.Ceiling(Math.Max(1, segments.Max(s => s.Duration.TotalSeconds)));
    var playlist = new HlsPlaylistBuilder();
    playlist.AppendHeader(targetDur, startOffset);
    AppendSegments(playlist, segments, cameraId);
    playlist.AppendEndList();
    return playlist.Build();
  }

  private static void AppendSegments(HlsPlaylistBuilder playlist, List<HlsSegment> segments, string cameraId)
  {
    var prevPath = "";
    for (var i = 0; i < segments.Count; i++)
    {
      var s = segments[i];
      if (i > 0 && s.FilePath != prevPath)
        playlist.AppendDiscontinuity();
      prevPath = s.FilePath;

      var qParams = new List<KeyValuePair<string, string?>>
      {
        new("cameraId", cameraId),
        new("path", s.FilePath)
      };
      if (s.ByteRange is { } br)
      {
        if (br.Offset > 0) qParams.Add(new("offset", br.Offset.ToString()));
        if (br.Length > 0) qParams.Add(new("length", br.Length.ToString()));
      }

      playlist.AppendSegment("/api/arch/hls/ts" + QueryString.Create(qParams), s.Duration.TotalSeconds);
    }
  }

  private async Task<double> RecordingWallDurationSecAsync(
    string cameraId, FsRecording rec, DateTime refWall, CancellationToken ct)
  {
    if (rec.EndTs != null)
    {
      var d = rec.EndTs.ToDateTime() - rec.StartTs.ToDateTime();
      if (d.TotalSeconds > 0 && !double.IsNaN(d.TotalSeconds))
        return d.TotalSeconds;
    }

    var d2 = await ClipWallDurationAsync(cameraId, rec.FilePath, refWall, ct);
    return d2.TotalSeconds > 0 && !double.IsNaN(d2.TotalSeconds) ? d2.TotalSeconds : 3600;
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

  private async Task<HlsSegment?> BuildRecordingSegmentAsync(
    FsRecording rec, DateTime wallFrom, DateTime wallTo, string cameraId, CancellationToken ct)
  {
    var wallStart = rec.StartTs.ToDateTime();
    if (wallTo <= wallStart)
      return null;

    var refT = wallStart > wallFrom ? wallStart : wallFrom;
    var durSec = await RecordingWallDurationSecAsync(cameraId, rec, refT, ct);
    var dur = TimeSpan.FromSeconds(Math.Max(durSec, 0.001));
    return new HlsSegment(rec.FilePath, null, wallStart, wallStart + dur);
  }

  private static double ComputeStartOffset(List<HlsSegment> segments, DateTime fromWall)
  {
    var acc = 0.0;
    foreach (var s in segments)
    {
      if (fromWall < s.WallStart) return acc;
      if (fromWall <= s.WallEnd) return acc + (fromWall - s.WallStart).TotalSeconds;
      acc += s.Duration.TotalSeconds;
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
}
