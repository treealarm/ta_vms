using Dapr.Client;
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
  ICameraManager cameraManager,
  ServerPairRegistry registry,
  LinkGenerator linkGenerator) : IHlsArchiveService
{
  private async Task<FsService.FsServiceClient?> ResolveAsync(string cameraId, CancellationToken ct)
  {
    var cam = await cameraManager.Get(cameraId, ct);
    return cam?.ServerId is { } sid ? registry.GetFsClient(sid) : null;
  }

  public async Task<HlsPlaylistResult> BuildPreviewPlaylistAsync(
    string cameraId, TimelineSlice range, CancellationToken ct)
  {
    var fsClient = await ResolveAsync(cameraId, ct);
    if (fsClient is null)
      return new HlsPlaylistResult.NotFound("camera not found or has no media server");

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

    var startOffset = ClampStartOffset(segments, ComputeStartOffset(segments, range.Start));

    return new HlsPlaylistResult.Ok(await BuildPlaylistAsync(fsClient, segments, ct, startOffset));
  }

  public async Task<HlsPlaylistResult> BuildFilesPlaylistAsync(
    string cameraId, string startClipUid, uint count, CancellationToken ct, DateTime? from = null)
  {
    var fsClient = await ResolveAsync(cameraId, ct);
    if (fsClient is null)
      return new HlsPlaylistResult.NotFound("camera not found or has no media server");

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

    double? startOffset = from is { } fromWall
      ? ClampStartOffset(segments, ComputeStartOffset(segments, fromWall))
      : null;

    return new HlsPlaylistResult.Ok(await BuildPlaylistAsync(fsClient, segments, ct, startOffset));
  }

  public async Task<HlsPlaylistResult> BuildEventPlaylistAsync(
    string eventId, string? streamName, CancellationToken ct)
  {
    // Fan-out to all FS instances — event_id belongs to the FS that stored it.
    GetRecordingsResponse? clipsResp = null;
    FsService.FsServiceClient? eventFsClient = null;
    foreach (var fsId in registry.AllFsIds)
    {
      var candidate = new FsService.FsServiceClient(DaprClient.CreateInvocationInvoker(fsId));
      try
      {
        clipsResp = await candidate.GetClipsForEventAsync(
          new GetClipsForEventRequest { EventId = eventId }, cancellationToken: ct);
        eventFsClient = candidate;
        break;
      }
      catch (RpcException ex) when (ex.StatusCode == Grpc.Core.StatusCode.NotFound) { /* try next */ }
      catch (RpcException) { /* FS unavailable — try next */ }
    }

    if (clipsResp is null || eventFsClient is null)
      return new HlsPlaylistResult.NotFound("event not found");

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

    return new HlsPlaylistResult.Ok(await BuildPlaylistAsync(eventFsClient, segments, ct));
  }

  private async Task<string> BuildPlaylistAsync(
    FsService.FsServiceClient fsClient, List<HlsSegment> segments, CancellationToken ct, double? startOffset = null)
  {
    var sidxCache = await BuildSidxCacheAsync(fsClient, segments, ct);

    var maxDur = 1.0;
    foreach (var s in segments)
    {
      if (sidxCache.TryGetValue(s.ClipUid, out var idx) && idx?.Fragments.Count > 0)
        maxDur = Math.Max(maxDur, idx.Fragments.Max(f => f.Duration));
      else
        maxDur = Math.Max(maxDur, s.Duration.TotalSeconds);
    }

    var playlist = new HlsPlaylistBuilder();
    playlist.AppendHeader((int)Math.Ceiling(maxDur), startOffset);
    AppendSegments(playlist, segments, sidxCache);
    playlist.AppendEndList();
    return playlist.Build();
  }

  private static async Task<Dictionary<string, Fmp4SidxParser.SegmentIndex?>> BuildSidxCacheAsync(
    FsService.FsServiceClient fsClient, List<HlsSegment> segments, CancellationToken ct)
  {
    var cache = new Dictionary<string, Fmp4SidxParser.SegmentIndex?>();
    foreach (var s in segments)
    {
      if (s.FilePath.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) && !cache.ContainsKey(s.ClipUid))
        cache[s.ClipUid] = await Fmp4SidxParser.ReadAsync(fsClient, s.ClipUid, ct);
    }
    return cache;
  }

  private void AppendSegments(
    HlsPlaylistBuilder playlist, List<HlsSegment> segments,
    IReadOnlyDictionary<string, Fmp4SidxParser.SegmentIndex?> sidxCache)
  {
    var prevClipUid = "";
    var mapEmittedForClip = "";

    for (var i = 0; i < segments.Count; i++)
    {
      var s = segments[i];
      var isMp4 = s.FilePath.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase);
      sidxCache.TryGetValue(s.ClipUid, out var idx);

      if (isMp4 && idx is null)
      {
        // Couldn't locate moov/ftyp for this fmp4 clip (corrupt or truncated file,
        // e.g. one written across a recording interruption) — there is no valid
        // #EXT-X-MAP to emit for it, and serving its raw bytes without one breaks
        // MSE parsing (bufferAppendError). Since a range can now stitch many clips
        // into one HLS source, one bad clip would otherwise take the whole range
        // down instead of just itself. Skip it; the surrounding clips still play.
        Console.Error.WriteLine($"AppendSegments: skipping unindexable fmp4 clip {s.ClipUid} ({s.FilePath})");
        continue;
      }

      if (prevClipUid.Length > 0 && s.ClipUid != prevClipUid)
        playlist.AppendDiscontinuity();
      prevClipUid = s.ClipUid;

      if (isMp4)
      {
        if (s.ClipUid != mapEmittedForClip)
        {
          playlist.AppendMap(BuildSegmentUri(s.ClipUid, new ByteRange(0, idx!.InitSize)));
          mapEmittedForClip = s.ClipUid;
        }

        if (idx!.Fragments.Count > 0)
        {
          // sidx found: one EXTINF per moof fragment
          foreach (var frag in idx.Fragments)
            playlist.AppendSegment(
              BuildSegmentUri(s.ClipUid, new ByteRange(frag.Offset, frag.Size)),
              frag.Duration);
        }
        else
        {
          // moov found but no mfra index: serve media part as one whole segment
          playlist.AppendSegment(
            BuildSegmentUri(s.ClipUid, new ByteRange(idx.InitSize, 0)),
            s.Duration.TotalSeconds);
        }
        continue;
      }

      playlist.AppendSegment(BuildSegmentUri(s.ClipUid, s.ByteRange), s.Duration.TotalSeconds);
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

  private static double ClampStartOffset(List<HlsSegment> segments, double startOffset)
  {
    var playlistDur = segments.Sum(s => s.Duration.TotalSeconds);
    if (playlistDur > 0 && startOffset >= playlistDur)
      startOffset = playlistDur - 0.001;
    if (startOffset < 0 || double.IsNaN(startOffset))
      startOffset = 0;
    return startOffset;
  }
}
