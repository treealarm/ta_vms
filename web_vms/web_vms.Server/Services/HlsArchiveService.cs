using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.AspNetCore.Routing;
using Vms.Fs;
using FsRecording = Vms.Fs.Recording;

namespace web_vms;

public sealed record ByteRange(ulong Offset, ulong Length);

public sealed record HlsSegment(
  string ClipUid,
  ByteRange? ByteRange,
  TimelineSlice Wall,
  string FilePath = "")
{
  public TimeSpan Duration => Wall.Duration;
}

public sealed class HlsArchiveService(
  FsService.FsServiceClient fsClient,
  LinkGenerator linkGenerator) : IHlsArchiveService
{
  public async Task<HlsPlaylistResult> BuildPreviewPlaylistAsync(
    string cameraId, TimelineSlice range, CancellationToken ct)
  {
    var fsResp = await fsClient.GetRecordingsAsync(new GetRecordingsFromToRequest
    {
      CameraId = cameraId,
      FromTs = Timestamp.FromDateTime(range.Start),
      ToTs = Timestamp.FromDateTime(range.End),
      Limit = 500
    }, cancellationToken: ct);

    var kept = fsResp.Recordings
      .Where(r => !string.IsNullOrWhiteSpace(r.FilePath) && !string.IsNullOrWhiteSpace(r.ClipUid))
      .OrderBy(r => r.StartTs.ToDateTime())
      .ToList();

    if (kept.Count == 0)
      return new HlsPlaylistResult.NotFound("no recordings in range");

    var segments = new List<HlsSegment>();
    foreach (var rec in kept)
    {
      StatArchiveTsResponse stat;
      try
      {
        stat = await fsClient.StatArchiveTsAsync(
          new StatArchiveTsRequest { ClipUid = rec.ClipUid }, cancellationToken: ct);
      }
      catch (RpcException)
      {
        return new HlsPlaylistResult.Error("stat archive failed");
      }

      if (!stat.Exists)
        continue;

      var seg = BuildRecordingSegment(rec, range);
      if (seg is not null)
        segments.Add(seg);
    }

    if (segments.Count == 0)
      return new HlsPlaylistResult.NotFound("no recordings in range");

    var startOffset = ComputeStartOffset(segments, range.Start);
    var playlistDur = segments.Sum(s => s.Duration.TotalSeconds);
    if (playlistDur > 0 && startOffset >= playlistDur)
      startOffset = playlistDur - 0.001;
    if (startOffset < 0 || double.IsNaN(startOffset))
      startOffset = 0;

    return new HlsPlaylistResult.Ok(await BuildPlaylistAsync(cameraId, segments, ct, startOffset));
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

    var segments = clipsResp.Recordings.Select(rec =>
    {
      var wallStart = rec.StartTs.ToDateTime();
      var wall = new TimelineSlice(wallStart, wallStart + RecordingWallDuration(rec));
      return new HlsSegment(rec.ClipUid, null, wall, rec.FilePath ?? "");
    }).ToList();

    return new HlsPlaylistResult.Ok(await BuildPlaylistAsync(cameraId, segments, ct));
  }

  public async Task<HlsPlaylistResult> BuildEventPlaylistAsync(
    string eventId, string? streamName, CancellationToken ct)
  {
    GetRecordingsResponse clipsResp;
    try
    {
      clipsResp = await fsClient.GetClipsForEventAsync(
        new GetClipsForEventRequest { EventId = eventId }, cancellationToken: ct);
    }
    catch (RpcException ex) when (ex.StatusCode == Grpc.Core.StatusCode.NotFound)
    {
      return new HlsPlaylistResult.NotFound(ex.Status.Detail);
    }
    catch (RpcException ex)
    {
      return new HlsPlaylistResult.Error(ex.Status.Detail);
    }

    var clips = clipsResp.Recordings
      .Where(r => !string.IsNullOrWhiteSpace(r.ClipUid))
      .Where(r => streamName is null || r.StreamName == streamName)
      .OrderBy(r => r.StartTs.ToDateTime())
      .ToList();

    if (clips.Count == 0)
      return new HlsPlaylistResult.NotFound("no recordings for this stream in event");

    var segments = clips.Select(rec =>
    {
      var wallStart = rec.StartTs.ToDateTime();
      return new HlsSegment(rec.ClipUid, null,
        new TimelineSlice(wallStart, wallStart + RecordingWallDuration(rec)),
        rec.FilePath ?? "");
    }).ToList();

    return new HlsPlaylistResult.Ok(await BuildPlaylistAsync(clips[0].CameraId, segments, ct));
  }

  private async Task<string> BuildPlaylistAsync(
    string cameraId, List<HlsSegment> segments, CancellationToken ct, double? startOffset = null)
  {
    var targetDur = (int)Math.Ceiling(Math.Max(1, segments.Max(s => s.Duration.TotalSeconds)));
    var playlist = new HlsPlaylistBuilder();
    playlist.AppendHeader(targetDur, startOffset);
    await AppendSegmentsAsync(playlist, segments, cameraId, ct);
    playlist.AppendEndList();
    return playlist.Build();
  }

  private async Task AppendSegmentsAsync(
    HlsPlaylistBuilder playlist, List<HlsSegment> segments, string cameraId, CancellationToken ct)
  {
    var prevClipUid = "";
    var mapEmittedForClip = "";

    for (var i = 0; i < segments.Count; i++)
    {
      var s = segments[i];
      if (i > 0 && s.ClipUid != prevClipUid)
        playlist.AppendDiscontinuity();
      prevClipUid = s.ClipUid;

      var isFmp4 = s.FilePath.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase);

      if (isFmp4 && s.ClipUid != mapEmittedForClip)
      {
        var initSize = await Fmp4InitParser.ReadInitSizeAsync(fsClient, s.ClipUid, ct);
        if (initSize is { } n)
        {
          var initUri = BuildSegmentUri(s.ClipUid, byteRange: new ByteRange(0, n));
          playlist.AppendMap(initUri);
          mapEmittedForClip = s.ClipUid;

          // Rebuild segment with media offset so the player skips the init bytes
          segments[i] = s with { ByteRange = new ByteRange(n, 0) };
          s = segments[i];
        }
      }

      var uri = BuildSegmentUri(s.ClipUid, s.ByteRange);
      playlist.AppendSegment(uri, s.Duration.TotalSeconds);
    }
  }

  private string BuildSegmentUri(string clipUid, ByteRange? byteRange)
  {
    var values = new RouteValueDictionary { ["clipUid"] = clipUid };
    if (byteRange is { } br)
    {
      if (br.Offset > 0) values["offset"] = br.Offset;
      if (br.Length > 0) values["length"] = br.Length;
    }

    return linkGenerator.GetPathByAction(
      action: nameof(ArchController.ServeArchiveTs),
      controller: "Arch",
      values: values)
      ?? throw new InvalidOperationException("Cannot generate segment URI");
  }

  // Used when EndTs is absent (in-progress or incomplete recording).
  // A real fix is to ensure the server always populates EndTs for completed recordings.
  private static readonly TimeSpan FallbackDuration = TimeSpan.FromHours(1);

  private static TimeSpan RecordingWallDuration(FsRecording rec)
  {
    if (rec.EndTs != null)
    {
      var d = rec.EndTs.ToDateTime() - rec.StartTs.ToDateTime();
      if (d > TimeSpan.Zero && !double.IsNaN(d.TotalSeconds))
        return d;
    }

    return FallbackDuration;
  }

  private static HlsSegment? BuildRecordingSegment(FsRecording rec, TimelineSlice queryRange)
  {
    var wallStart = rec.StartTs.ToDateTime();
    if (queryRange.End <= wallStart)
      return null;

    var wall = new TimelineSlice(wallStart, wallStart + RecordingWallDuration(rec));
    return new HlsSegment(rec.ClipUid, null, wall, rec.FilePath ?? "");
  }

  private static double ComputeStartOffset(List<HlsSegment> segments, DateTime fromWall)
  {
    var acc = 0.0;
    foreach (var s in segments)
    {
      if (fromWall < s.Wall.Start) return acc;
      if (s.Wall.Contains(fromWall)) return acc + s.Wall.OffsetOf(fromWall);
      acc += s.Duration.TotalSeconds;
    }
    return acc;
  }
}
