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
  ArchiveOwnershipCache ownership,
  LinkGenerator linkGenerator) : IHlsArchiveService
{
  private async Task<FsService.FsServiceClient?> ResolveAsync(string cameraId, CancellationToken ct)
  {
    var cam = await cameraManager.Get(cameraId, ct);
    return cam?.ServerId is { } sid ? registry.GetFsClient(sid) : null;
  }

  // Wall-clock playlist: "give me this camera's archive between these two instants". This is what
  // drives the synchronized wall — every cell asks for the same window and follows PDT.
  public async Task<HlsPlaylistResult> BuildPreviewPlaylistAsync(
    string cameraId, string? streamName, TimelineSlice range, CancellationToken ct)
  {
    var fsClient = await ResolveAsync(cameraId, ct);
    if (fsClient is null)
      return new HlsPlaylistResult.NotFound("camera not found or has no media server");

    var req = new GetRecordingsFromToRequest
    {
      CameraId = cameraId,
      FromTs = Timestamp.FromDateTime(range.Start),
      ToTs = Timestamp.FromDateTime(range.End),
      Limit = 500
    };
    // Without a stream filter a main+sub camera returns both streams interleaved, producing a
    // playlist whose segments overlap in wall time. GetClipsFrom (from-files.m3u8) avoids this by
    // inheriting the anchor clip's stream; here the caller has to name it.
    if (!string.IsNullOrWhiteSpace(streamName))
      req.StreamName = streamName;

    GetRecordingsResponse fsResp;
    try
    {
      fsResp = await fsClient.GetRecordingsAsync(req, cancellationToken: ct);
    }
    catch (RpcException ex)
    {
      return new HlsPlaylistResult.Error(ex.Status.Detail);
    }

    var kept = fsResp.Recordings
      .Where(r => !string.IsNullOrWhiteSpace(r.FilePath) && !string.IsNullOrWhiteSpace(r.ClipUid))
      .Where(r => r.StartTs.ToDateTime() < range.End)
      .OrderBy(r => r.StartTs.ToDateTime())
      .ToList();

    if (kept.Count == 0)
      return new HlsPlaylistResult.NotFound("no recordings in range");

    // The recordings are already here and carry their camera — record who owns each clip so the
    // segment endpoint can authorize it without a lookup of its own.
    ownership.PrimeClips(kept);

    // Clip existence is answered by GetClipIndexes (Found=false for a missing or unreadable file),
    // so there is no separate StatArchiveTs pass — that used to cost one gRPC round-trip per clip,
    // per cell, on every seek.
    var cacheResult = await BuildSidxCacheAsync(fsClient, kept.Select(r => r.ClipUid), ct);
    if (cacheResult.Error is { } cacheErr)
      return cacheErr;

    var segments = ResolveWallDurations(kept, cacheResult.Cache);
    if (segments.Count == 0)
      return new HlsPlaylistResult.NotFound("no recordings in range");

    var startOffset = ClampStartOffset(segments, ComputeStartOffset(segments, range.Start));

    return BuildPlaylist(segments, cacheResult.Cache, startOffset);
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

    var ordered = clipsResp.Recordings.OrderBy(r => r.StartTs.ToDateTime()).ToList();
    ownership.PrimeClips(ordered);

    var cacheResult = await BuildSidxCacheAsync(fsClient, ordered.Select(r => r.ClipUid), ct);
    if (cacheResult.Error is { } cacheErr)
      return cacheErr;

    var segments = ResolveWallDurations(ordered, cacheResult.Cache);
    if (segments.Count == 0)
      return new HlsPlaylistResult.NotFound("no clips found");

    double? startOffset = from is { } fromWall
      ? ClampStartOffset(segments, ComputeStartOffset(segments, fromWall))
      : null;

    return BuildPlaylist(segments, cacheResult.Cache, startOffset);
  }

  public async Task<HlsPlaylistResult> BuildEventPlaylistAsync(
    string eventUuid, string? streamName, DateTime? fromTs, DateTime? toTs, CancellationToken ct)
  {
    // Fan-out to all FS instances (parallel, short deadline) — the episode belongs to the FS that
    // stored it; keep talking to the instance that actually has the clips.
    var clipResults = await registry.FanOutFsAsync(
      (c, o) => c.GetClipsForEventAsync(new GetClipsForEventRequest { EventUuid = eventUuid }, o).ResponseAsync, ct);
    var hit = clipResults.FirstOrDefault(r => r.Value.Recordings.Count > 0) ?? clipResults.FirstOrDefault();

    if (hit is null)
      return new HlsPlaylistResult.NotFound("event not found");

    var clipsResp = hit.Value;
    var eventFsClient = hit.Client;

    ownership.PrimeClips(clipsResp.Recordings);
    if (clipsResp.Recordings.FirstOrDefault()?.CameraId is { } eventCamera)
      ownership.PrimeEvent(eventUuid, eventCamera);

    var clips = clipsResp.Recordings
      .Where(r => !string.IsNullOrWhiteSpace(r.ClipUid))
      .Where(r => streamName is null || r.StreamName == streamName)
      .Where(r => fromTs is null || r.StartTs.ToDateTime().ToUniversalTime() >= fromTs)
      .Where(r => toTs is null || r.StartTs.ToDateTime().ToUniversalTime() <= toTs)
      .OrderBy(r => r.StartTs.ToDateTime())
      .ToList();

    if (clips.Count == 0)
      return new HlsPlaylistResult.NotFound("no recordings for this stream in event");

    var cacheResult = await BuildSidxCacheAsync(eventFsClient, clips.Select(r => r.ClipUid), ct);
    if (cacheResult.Error is { } cacheErr)
      return cacheErr;

    var segments = ResolveWallDurations(clips, cacheResult.Cache);
    if (segments.Count == 0)
      return new HlsPlaylistResult.NotFound("no recordings for this stream in event");

    return BuildPlaylist(segments, cacheResult.Cache, null);
  }

  private HlsPlaylistResult BuildPlaylist(
    List<HlsSegment> segments,
    IReadOnlyDictionary<string, Fmp4SegmentIndex?> sidxCache,
    double? startOffset)
  {
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

    // Every clip in the window turned out to be unindexable (see the skip in AppendSegments).
    // Returning the structurally valid but segment-less playlist would give the player a 200 it
    // silently stalls on — and the response is cached for a minute, so the retry stalls too.
    if (playlist.SegmentCount == 0)
      return new HlsPlaylistResult.Error("no playable segments in range");

    playlist.AppendEndList();
    return new HlsPlaylistResult.Ok(playlist.Build());
  }

  private sealed record SidxCacheResult(
    Dictionary<string, Fmp4SegmentIndex?> Cache,
    HlsPlaylistResult.Error? Error);

  // The fmp4 index (init size + per-moof offsets) of every clip in the playlist, fetched in ONE
  // batch call. VmsFs reads/parses each clip locally and caches the result, so building a manifest
  // over 100+ clips costs a single round-trip instead of several file-range reads per clip (which
  // used to take tens of seconds and trip the player's manifestLoadTimeout).
  private static async Task<SidxCacheResult> BuildSidxCacheAsync(
    FsService.FsServiceClient fsClient, IEnumerable<string> clipUids, CancellationToken ct)
  {
    var uids = clipUids.Where(u => !string.IsNullOrWhiteSpace(u)).Distinct().ToList();

    var cache = new Dictionary<string, Fmp4SegmentIndex?>();
    if (uids.Count == 0) return new SidxCacheResult(cache, null);

    var req = new GetClipIndexesRequest();
    req.ClipUids.AddRange(uids);

    GetClipIndexesResponse resp;
    try
    {
      resp = await fsClient.GetClipIndexesAsync(req, cancellationToken: ct);
    }
    catch (RpcException ex)
    {
      return new SidxCacheResult(cache, new HlsPlaylistResult.Error($"clip index lookup failed: {ex.Status.Detail}"));
    }

    foreach (var ci in resp.Indexes)
    {
      cache[ci.ClipUid] = ci.Found
        ? new Fmp4SegmentIndex(
            ci.InitSize,
            ci.Fragments.Select(f => new Fmp4FragmentEntry(f.Offset, f.Size, f.Duration)).ToList())
        : null;
    }
    return new SidxCacheResult(cache, null);
  }

  private void AppendSegments(
    HlsPlaylistBuilder playlist, List<HlsSegment> segments,
    IReadOnlyDictionary<string, Fmp4SegmentIndex?> sidxCache)
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

      // Wall time advances within the clip by the segment durations we are about to emit, and is
      // re-anchored to the clip's own start on every new clip. Anchoring per clip is what keeps a
      // gap between clips from accumulating into the mapping — the client derives wall time from
      // PDT alone, so a drifting cursor here would desynchronize the whole wall.
      var wallCursor = s.Wall.Start;

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
          {
            playlist.AppendProgramDateTime(wallCursor);
            playlist.AppendSegment(
              BuildSegmentUri(s.ClipUid, new ByteRange(frag.Offset, frag.Size)),
              frag.Duration);
            wallCursor += TimeSpan.FromSeconds(frag.Duration);
          }
        }
        else
        {
          // moov found but no mfra index: serve media part as one whole segment
          playlist.AppendProgramDateTime(wallCursor);
          playlist.AppendSegment(
            BuildSegmentUri(s.ClipUid, new ByteRange(idx.InitSize, 0)),
            s.Duration.TotalSeconds);
        }
        continue;
      }

      playlist.AppendProgramDateTime(wallCursor);
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

  // Wall duration of every clip, in order. A wrong duration here is not cosmetic: it shifts every
  // #EXT-X-PROGRAM-DATE-TIME after it and the #EXT-X-START offset, i.e. it desynchronizes the wall.
  // So there is no invented fallback — a clip whose length cannot be established is dropped.
  private static List<HlsSegment> ResolveWallDurations(
    List<FsRecording> ordered,
    IReadOnlyDictionary<string, Fmp4SegmentIndex?> sidxCache)
  {
    var segments = new List<HlsSegment>(ordered.Count);

    for (var i = 0; i < ordered.Count; i++)
    {
      var rec = ordered[i];
      var wallStart = rec.StartTs.ToDateTime();
      var duration = ResolveOneWallDuration(rec, wallStart, sidxCache,
        next: i + 1 < ordered.Count ? ordered[i + 1] : null);

      if (duration is not { } d)
      {
        Console.Error.WriteLine($"ResolveWallDurations: dropping clip {rec.ClipUid} — no usable duration");
        continue;
      }

      segments.Add(new HlsSegment(rec.ClipUid, null,
        new TimelineSlice(wallStart, wallStart + d), rec.FilePath ?? ""));
    }

    return segments;
  }

  private static TimeSpan? ResolveOneWallDuration(
    FsRecording rec, DateTime wallStart,
    IReadOnlyDictionary<string, Fmp4SegmentIndex?> sidxCache,
    FsRecording? next)
  {
    if (rec.EndTs != null)
    {
      var d = rec.EndTs.ToDateTime() - wallStart;
      if (d > TimeSpan.Zero && !double.IsNaN(d.TotalSeconds))
        return d;
    }

    // In-progress or incomplete recording (EndTs never written). The fragment index is the next
    // best source: it is derived from the file's own timestamps.
    if (sidxCache.TryGetValue(rec.ClipUid, out var idx) && idx is { Fragments.Count: > 0 })
    {
      var sum = idx.Fragments.Sum(f => f.Duration);
      if (sum > 0 && !double.IsNaN(sum))
        return TimeSpan.FromSeconds(sum);
    }

    // Last resort: the clip runs until the next one starts.
    if (next is not null)
    {
      var d = next.StartTs.ToDateTime() - wallStart;
      if (d > TimeSpan.Zero)
        return d;
    }

    return null;
  }

  // Where inside the playlist the requested instant falls. Note this is expressed only via
  // #EXT-X-START — the head segment is never shortened to "start at" the requested time. Its bytes
  // still begin at the start of the chunk, so a shortened #EXTINF would disagree with its PDT and
  // put every later mapping off by the amount trimmed.
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
