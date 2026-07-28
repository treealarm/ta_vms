using Dapr.Client;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.AspNetCore.Mvc;
using Vms.Control;
using Vms.Fs;
using web_vms;
using web_vms.Types.Dto;

namespace web_vms.Server.Controllers;

[Route("api/[controller]")]
[ApiController]
public sealed class FSController(
  ICameraManager manager,
  ServerPairRegistry registry) : ControllerBase
{
  private static readonly TimeSpan FrameJpegTimeout = TimeSpan.FromSeconds(15);

  private static CameraControlService.CameraControlServiceClient GetMediaClient(string serverId) =>
    new(DaprClient.CreateInvocationInvoker(serverId));

  private static CallOptions FrameCallOptions(CancellationToken ct) =>
    new(deadline: DateTime.UtcNow.Add(FrameJpegTimeout), cancellationToken: ct);

  private static DateTime ToUtc(DateTime dt) =>
    dt.Kind switch
    {
      DateTimeKind.Utc => dt,
      DateTimeKind.Local => dt.ToUniversalTime(),
      _ => DateTime.SpecifyKind(dt, DateTimeKind.Utc)
    };

  private static RecordingDto ToRecordingDto(Vms.Fs.Recording r) => new()
  {
    FilePath   = r.FilePath,
    StreamName = r.StreamName,
    StartTime  = r.StartTs.ToDateTime(),
    EndTime    = r.EndTs?.ToDateTime(),
    SizeBytes  = r.SizeBytes,
    ClipUid    = r.ClipUid
  };

  private static RecordingDto? NearestRecording(IReadOnlyList<RecordingDto> recordings, DateTime targetUtc)
  {
    if (recordings.Count == 0)
      return null;

    RecordingDto nearest = recordings[0];
    var minDelta = AbsMillis(nearest.StartTime - targetUtc);
    foreach (var rec in recordings.Skip(1))
    {
      var d = AbsMillis(rec.StartTime - targetUtc);
      if (d < minDelta)
      {
        minDelta = d;
        nearest = rec;
      }
    }

    return nearest;
  }

  private static long AbsMillis(TimeSpan d) => Math.Abs((long)d.TotalMilliseconds);

  [HttpPost("recordings")]
  public async Task<IActionResult> GetRecordings([FromBody] RecordingsQueryDto query, CancellationToken ct)
  {
    if (string.IsNullOrWhiteSpace(query.CameraId))
      return BadRequest(new { error = "cameraId is required" });

    var cam = await manager.Get(query.CameraId);
    if (cam is null)
      return NotFound();

    var limit = query.Limit ?? 50;

    var fsClient = registry.GetFsClient(cam.ServerId);
    GetRecordingsResponse grpcResp;
    if (query.Timestamp.HasValue)
    {
      var req = new GetRecordingsAroundRequest
      {
        CameraId  = query.CameraId,
        Timestamp = Timestamp.FromDateTime(ToUtc(query.Timestamp.Value)),
        Limit     = limit
      };
      if (!string.IsNullOrEmpty(query.StreamName))
        req.StreamName = query.StreamName;
      grpcResp = await fsClient.GetRecordingsAroundAsync(req, cancellationToken: ct);
    }
    else
    {
      var req = new GetRecordingsFromToRequest
      {
        CameraId = query.CameraId,
        Limit    = limit
      };
      if (!string.IsNullOrEmpty(query.StreamName))
        req.StreamName = query.StreamName;
      if (query.FromUtc.HasValue)
        req.FromTs = Timestamp.FromDateTime(ToUtc(query.FromUtc.Value));
      if (query.ToUtc.HasValue)
        req.ToTs = Timestamp.FromDateTime(ToUtc(query.ToUtc.Value));

      grpcResp = await fsClient.GetRecordingsAsync(req, cancellationToken: ct);
    }

    var recordings = grpcResp.Recordings.Select(ToRecordingDto).ToList();
    var dto = new RecordingsResponseDto { Recordings = recordings };

    if (query.Timestamp.HasValue)
      dto.Nearest = NearestRecording(recordings, ToUtc(query.Timestamp.Value));

    return Ok(dto);
  }

  [HttpPost("recording-ranges")]
  public async Task<IActionResult> GetRecordingRanges([FromBody] RecordingsQueryDto query, CancellationToken ct)
  {
    if (string.IsNullOrWhiteSpace(query.CameraId))
      return BadRequest(new { error = "cameraId is required" });

    var rangeCam = await manager.Get(query.CameraId);
    if (rangeCam is null)
      return NotFound();

    var fsClient = registry.GetFsClient(rangeCam.ServerId);
    var grpcReq = new GetRecordingRangesRequest
    {
      CameraId       = query.CameraId,
      Limit          = query.Limit ?? 50,
      GapToleranceMs = 1000
    };
    if (!string.IsNullOrEmpty(query.StreamName))
      grpcReq.StreamName = query.StreamName;

    if (query.Timestamp.HasValue)
      grpcReq.Timestamp = Timestamp.FromDateTime(ToUtc(query.Timestamp.Value));
    else
    {
      if (query.FromUtc.HasValue)
        grpcReq.FromTs = Timestamp.FromDateTime(ToUtc(query.FromUtc.Value));
      if (query.ToUtc.HasValue)
        grpcReq.ToTs = Timestamp.FromDateTime(ToUtc(query.ToUtc.Value));
    }

    var grpcResp = await fsClient.GetRecordingRangesAsync(grpcReq, cancellationToken: ct);

    var dto = new RecordingRangesResponseDto
    {
      Ranges = grpcResp.Ranges.Select(r => new RecordingRangeDto
      {
        Recordings = r.Recordings.Select(ToRecordingDto).ToList()
      }).ToList()
    };

    return Ok(dto);
  }

  [HttpPost("recording-coverage")]
  public async Task<IActionResult> GetRecordingCoverage([FromBody] RecordingCoverageQueryDto query, CancellationToken ct)
  {
    if (string.IsNullOrWhiteSpace(query.CameraId))
      return BadRequest(new { error = "cameraId is required" });

    var coverageCam = await manager.Get(query.CameraId);
    if (coverageCam is null)
      return NotFound();

    var fsClient = registry.GetFsClient(coverageCam.ServerId);
    var grpcReq = new GetRecordingCoverageRequest
    {
      CameraId = query.CameraId,
      Granularity = query.Granularity switch
      {
        RecordingCoverageGranularityDto.Year => RecordingCoverageGranularity.RecordingCoverageYear,
        RecordingCoverageGranularityDto.Month => RecordingCoverageGranularity.RecordingCoverageMonth,
        _ => RecordingCoverageGranularity.RecordingCoverageDay,
      },
    };
    if (query.FromUtc.HasValue)
      grpcReq.FromTs = Timestamp.FromDateTime(ToUtc(query.FromUtc.Value));
    if (query.ToUtc.HasValue)
      grpcReq.ToTs = Timestamp.FromDateTime(ToUtc(query.ToUtc.Value));

    var grpcResp = await fsClient.GetRecordingCoverageAsync(grpcReq, cancellationToken: ct);

    return Ok(new RecordingCoverageResponseDto { Buckets = grpcResp.Buckets.ToList() });
  }

  [HttpPost("events")]
  public async Task<IActionResult> RetainEvent([FromBody] RetentionEventDto body, CancellationToken ct)
  {
    if (string.IsNullOrWhiteSpace(body.CameraId))
      return BadRequest(new { error = "cameraId is required" });

    var retainCam = await manager.Get(body.CameraId);
    if (retainCam is null)
      return NotFound();

    var fsClient = registry.GetFsClient(retainCam.ServerId);
    var grpcReq = new RetainEventRequest
    {
      CameraId        = body.CameraId,
      AlarmStartTs    = Timestamp.FromDateTime(ToUtc(body.AlarmStartTs)),
      TimeoutSeconds  = body.TimeoutSeconds,
    };

    if (!string.IsNullOrWhiteSpace(body.EventId))
      grpcReq.EventId = body.EventId!;
    if (!string.IsNullOrWhiteSpace(body.StreamName))
      grpcReq.StreamName = body.StreamName!;
    if (body.AlarmEndTs.HasValue)
      grpcReq.AlarmEndTs = Timestamp.FromDateTime(ToUtc(body.AlarmEndTs.Value));

    var resp = await fsClient.RetainEventAsync(grpcReq, cancellationToken: ct);
    if (resp.Status != Vms.Fs.OperationStatus.Types.Status.Success)
      return StatusCode(500, new { success = false, message = resp.Message });

    return Ok(new ApiResponse(true, "ok"));
  }

  [HttpGet("retention-events")]
  public async Task<IActionResult> ListRetentionEvents([FromQuery] int? limit, [FromQuery] string? before, CancellationToken ct)
  {
    var pageLimit = Math.Clamp(limit ?? 50, 1, 200);
    // Over-fetch episodes per instance so we can assemble reasonably complete groups from a fan-out
    // where one group's episodes may live on different FS instances. Pagination stays group-granular.
    var fetchLimit = (uint)Math.Min(200, pageLimit * 5);
    var grpcReq = new ListRetentionEventsRequest { Limit = fetchLimit };
    if (!string.IsNullOrWhiteSpace(before))
    {
      if (!DateTime.TryParse(before, null, System.Globalization.DateTimeStyles.RoundtripKind, out var bt))
        return BadRequest(new { error = "before must be RFC3339" });
      grpcReq.BeforeAlarmStartTs = Timestamp.FromDateTime(ToUtc(bt));
    }

    static EpisodeDto ToEpisode(RetentionEventRow e) => new()
    {
      EventUuid    = e.EventUuid,
      EventId      = e.EventId,
      ObjectId     = e.HasObjectId ? e.ObjectId : null,
      CameraId     = e.CameraId,
      StreamName   = e.HasStreamName ? e.StreamName : null,
      AlarmStartTs = e.AlarmStartTs.ToDateTime(),
      AlarmEndTs   = e.AlarmEndTs?.ToDateTime()
    };

    static EventGroupDto BuildGroup(string eventId, List<EpisodeDto> eps)
    {
      var ordered = eps.OrderBy(e => e.AlarmStartTs).ToList();
      return new EventGroupDto
      {
        EventId                = eventId,
        ObjectId               = ordered.Select(e => e.ObjectId).FirstOrDefault(o => o is not null),
        RepresentativeCameraId = ordered[0].CameraId,
        EarliestAlarmStart     = ordered[0].AlarmStartTs,
        LatestAlarmStart       = ordered[^1].AlarmStartTs,
        Episodes               = ordered,
      };
    }

    // Fan-out to all FS instances, then group the merged episodes by event_id (the group label
    // minted by the single-DB VmsAnalytics, so it is globally consistent across instances).
    var episodes = new List<EpisodeDto>();
    var moreOnInstance = false;
    var responses = await registry.FanOutFsAsync((c, o) => c.ListRetentionEventsAsync(grpcReq, o).ResponseAsync, ct);
    foreach (var resp in responses.Select(r => r.Value))
    {
      if (resp.HasMore) moreOnInstance = true;
      episodes.AddRange(resp.Events.Select(ToEpisode));
    }

    var allGroups = episodes
      .GroupBy(e => e.EventId)
      .Select(g => BuildGroup(g.Key, g.ToList()))
      .OrderByDescending(g => g.LatestAlarmStart)
      .ToList();

    var groups = allGroups.Take(pageLimit).ToList();
    // hasMore must also honor an instance that had more rows beyond fetchLimit — otherwise, when the
    // fetched episodes collapse into <= pageLimit groups, older events would become unreachable.
    var hasMore = moreOnInstance || allGroups.Count > pageLimit;

    // A shown group whose oldest fetched episode sits at the fetch floor may have older episodes past
    // the paged window, making its badge/earliest/episode-tree wrong. When that can happen, re-fetch
    // the full episode set for exactly the shown groups and rebuild them complete. (Common case: no
    // group touches the floor -> no extra round-trip.)
    if (moreOnInstance && groups.Count > 0)
    {
      var oldestFetched = episodes.Min(e => e.AlarmStartTs);
      var incompleteIds = groups.Where(g => g.EarliestAlarmStart <= oldestFetched).Select(g => g.EventId).ToList();
      if (incompleteIds.Count > 0)
      {
        var fullReq = new ListRetentionEventsRequest();
        fullReq.EventIds.AddRange(incompleteIds);
        var fullResponses = await registry.FanOutFsAsync((c, o) => c.ListRetentionEventsAsync(fullReq, o).ResponseAsync, ct);
        var episodesByEvent = fullResponses
          .SelectMany(r => r.Value.Events)
          .Select(ToEpisode)
          .GroupBy(e => e.EventId)
          .ToDictionary(g => g.Key, g => g.ToList());
        groups = groups
          .Select(g => episodesByEvent.TryGetValue(g.EventId, out var eps) ? BuildGroup(g.EventId, eps) : g)
          .ToList();
      }
    }

    // Cursor by the last shown group's LATEST alarm_start (consistent with the ordering). Using its
    // EARLIEST would skip any group whose episodes fall between that earliest and latest. The trade-off
    // is that a group straddling the boundary can reappear (partially) on the next page rather than be
    // hidden — an overlap is far preferable to silently dropping events.
    return Ok(new RetentionEventGroupsListDto
    {
      HasMore = hasMore,
      Groups  = groups,
      NextBeforeAlarmStart = hasMore && groups.Count > 0 ? groups[^1].LatestAlarmStart : null,
    });
  }

  [HttpGet("event-playlists")]
  public async Task<IActionResult> GetEventPlaylists([FromQuery] string eventUuid, CancellationToken ct)
  {
    if (string.IsNullOrWhiteSpace(eventUuid))
      return BadRequest(new { error = "eventUuid is required" });

    // Fan-out to all FS instances — an episode lives on the FS that stored it; take the one
    // that actually has clips for it.
    var clipResponses = await registry.FanOutFsAsync(
      (c, o) => c.GetClipsForEventAsync(new GetClipsForEventRequest { EventUuid = eventUuid }, o).ResponseAsync, ct);
    var clipsResp = (clipResponses.FirstOrDefault(r => r.Value.Recordings.Count > 0) ?? clipResponses.FirstOrDefault())?.Value;

    if (clipsResp is null)
      return NotFound(new { error = "event not found" });

    // Split a long event into <=3-min playback chunks: building one HLS source over hundreds of
    // clips reads a sidx per clip and trips the player's manifest timeout. Each (chunk, stream) is a
    // separate selectable source; a short event stays a single whole-event source per stream.
    var chunks = ChunkWindows(clipsResp.Recordings, TimeSpan.FromMinutes(3));

    string Url(string? stream, (DateTime From, DateTime To)? win)
    {
      var qs = $"eventUuid={Uri.EscapeDataString(eventUuid)}";
      if (stream is not null) qs += $"&streamName={Uri.EscapeDataString(stream)}";
      if (win is { } w) qs += $"&from={Uri.EscapeDataString(w.From.ToString("o"))}&to={Uri.EscapeDataString(w.To.ToString("o"))}";
      return $"/api/arch/hls/event.m3u8?{qs}";
    }

    object Entry(string? stream, (DateTime From, DateTime To)? win) => new
    {
      streamName  = stream,
      playlistUrl = Url(stream, win),
      fromTs      = win is { } w ? w.From : (DateTime?)null,
      toTs        = win is { } w2 ? w2.To : (DateTime?)null,
    };

    var entries = new List<object>();
    if (chunks.Count <= 1)
    {
      var streamNames = clipsResp.Recordings.Select(r => r.StreamName).Distinct().OrderBy(s => s).ToList();
      if (streamNames.Count > 1) entries.Add(Entry(null, null));
      entries.AddRange(streamNames.Select(s => Entry(s, null)));
    }
    else
    {
      foreach (var w in chunks)
      {
        var streamsInChunk = clipsResp.Recordings
          .Where(r => { var t = r.StartTs.ToDateTime().ToUniversalTime(); return t >= w.From && t <= w.To; })
          .Select(r => r.StreamName).Distinct().OrderBy(s => s).ToList();
        if (streamsInChunk.Count > 1) entries.Add(Entry(null, w));
        entries.AddRange(streamsInChunk.Select(s => Entry(s, w)));
      }
    }

    return Ok(new { streams = entries });
  }

  // Groups an event's clips into consecutive windows no longer than maxSpan, split on clip-start
  // boundaries so each clip belongs to exactly one window (returns [firstStart, lastStart] per window).
  private static List<(DateTime From, DateTime To)> ChunkWindows(IEnumerable<Vms.Fs.Recording> recordings, TimeSpan maxSpan)
  {
    var starts = recordings
      .Where(r => !string.IsNullOrWhiteSpace(r.ClipUid))
      .Select(r => r.StartTs.ToDateTime().ToUniversalTime())
      .OrderBy(t => t)
      .ToList();

    var chunks = new List<(DateTime From, DateTime To)>();
    if (starts.Count == 0) return chunks;

    var from = starts[0];
    var last = starts[0];
    foreach (var t in starts)
    {
      if (t - from >= maxSpan)
      {
        chunks.Add((from, last));
        from = t;
      }
      last = t;
    }
    chunks.Add((from, last));
    return chunks;
  }

  [HttpGet("event-clips")]
  public async Task<IActionResult> GetEventClips([FromQuery] string eventUuid, CancellationToken ct)
  {
    if (string.IsNullOrWhiteSpace(eventUuid))
      return BadRequest(new { error = "eventUuid is required" });

    // Fan-out to all FS instances — an episode lives on the FS that stored it; take the one
    // that actually has clips for it.
    var clipResponses = await registry.FanOutFsAsync(
      (c, o) => c.GetClipsForEventAsync(new GetClipsForEventRequest { EventUuid = eventUuid }, o).ResponseAsync, ct);
    var clipsResp = (clipResponses.FirstOrDefault(r => r.Value.Recordings.Count > 0) ?? clipResponses.FirstOrDefault())?.Value;

    if (clipsResp is null)
      return NotFound(new { error = "event not found" });

    var clips = clipsResp.Recordings
      .Select(ToRecordingDto)
      .OrderBy(c => c.StartTime)
      .ToList();

    return Ok(new EventClipsResponseDto { Clips = clips });
  }

  [HttpGet("activity")]
  public async Task<IActionResult> GetFsActivity([FromQuery] int? limit, CancellationToken ct)
  {
    var lim = (uint)(limit ?? 100);
    var allEvents = new List<FsFileEventDto>();
    var responses = await registry.FanOutFsAsync((c, o) => c.GetFsActivityAsync(new GetFsActivityRequest { Limit = lim }, o).ResponseAsync, ct);
    foreach (var resp in responses.Select(r => r.Value))
    {
      allEvents.AddRange(resp.Events.Select(e => new FsFileEventDto
      {
        ClipUid    = e.ClipUid,
        FilePath   = e.FilePath,
        CameraId   = e.CameraId,
        StreamName = e.StreamName,
        Retained   = e.Retained,
        SizeBytes  = e.SizeBytes,
        OccurredAt = e.OccurredAt.ToDateTime(),
        EventType  = e.EventType,
        EndTs      = e.EndTs?.ToDateTime(),
      }));
    }

    var sorted = allEvents
      .OrderByDescending(e => e.OccurredAt)
      .Take((int)lim)
      .ToList();

    return Ok(new FsActivityResponseDto { Events = sorted });
  }

  [HttpGet("frame-jpeg")]
  public async Task<IActionResult> GetFrameJpeg(
    [FromQuery] string cameraId,
    [FromQuery] string? streamName,
    [FromQuery] string timestamp,
    CancellationToken ct)
  {
    if (string.IsNullOrWhiteSpace(cameraId))
      return BadRequest(new { error = "cameraId is required" });

    if (string.IsNullOrWhiteSpace(timestamp))
      return BadRequest(new { error = "timestamp is required" });

    if (!DateTime.TryParse(timestamp, null, System.Globalization.DateTimeStyles.RoundtripKind, out var tsUtc))
      return BadRequest(new { error = "timestamp must be RFC3339 / ISO 8601" });

    tsUtc = ToUtc(tsUtc);

    var cam = await manager.Get(cameraId);
    if (cam is null)
      return NotFound(new { error = "camera not found" });

    if (string.IsNullOrWhiteSpace(cam.ServerId))
      return StatusCode(503, new { error = "camera has no assigned media server" });

    var jpegFsClient = registry.GetFsClient(cam.ServerId);
    var fsReq = new GetRecordingsAroundRequest
    {
      CameraId  = cameraId,
      Timestamp = Timestamp.FromDateTime(tsUtc),
      Limit     = 3
    };
    if (!string.IsNullOrWhiteSpace(streamName))
      fsReq.StreamName = streamName;

    var fsResp = await jpegFsClient.GetRecordingsAroundAsync(fsReq, cancellationToken: ct);

    var rec = fsResp.Recordings
      .Where(r => r.StartTs.ToDateTime() <= tsUtc)
      .OrderByDescending(r => r.StartTs.ToDateTime())
      .FirstOrDefault();

    if (rec is null)
      return NotFound(new { error = "no recording found for the given timestamp" });

    var stat = await jpegFsClient.StatArchiveTsAsync(
      new StatArchiveTsRequest { ClipUid = rec.ClipUid }, cancellationToken: ct);

    if (!stat.Exists)
      return NotFound(new { error = stat.Message });

    var offsetMs = (long)(tsUtc - rec.StartTs.ToDateTime()).TotalMilliseconds;
    if (offsetMs < 0) offsetMs = 0;

    var mediaResp = await GetMediaClient(cam.ServerId).GetArchiveFrameJpegAsync(
      new ArchiveFrameJpegRequest { FilePath = stat.FilePath, OffsetMs = offsetMs },
      FrameCallOptions(ct));

    if (!mediaResp.Success)
      return StatusCode(500, new { error = mediaResp.Message });

    return File(mediaResp.Jpeg.ToByteArray(), "image/jpeg");
  }
}
