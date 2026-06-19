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
    var pageLimit = (uint)(limit ?? 50);
    var grpcReq = new ListRetentionEventsRequest { Limit = pageLimit };
    if (!string.IsNullOrWhiteSpace(before))
    {
      if (!DateTime.TryParse(before, null, System.Globalization.DateTimeStyles.RoundtripKind, out var bt))
        return BadRequest(new { error = "before must be RFC3339" });
      grpcReq.BeforeAlarmStartTs = Timestamp.FromDateTime(ToUtc(bt));
    }

    // Fan-out to all FS instances and merge results sorted by alarm_start desc.
    var allEvents = new List<RetentionEventRowDto>();
    var hasMore = false;
    foreach (var fsId in registry.AllFsIds)
    {
      var fsClient = new FsService.FsServiceClient(DaprClient.CreateInvocationInvoker(fsId));
      try
      {
        var resp = await fsClient.ListRetentionEventsAsync(grpcReq, cancellationToken: ct);
        if (resp.HasMore) hasMore = true;
        allEvents.AddRange(resp.Events.Select(e => new RetentionEventRowDto
        {
          EventId      = e.EventId,
          CameraId     = e.CameraId,
          StreamName   = e.HasStreamName ? e.StreamName : null,
          AlarmStartTs = e.AlarmStartTs.ToDateTime(),
          AlarmEndTs   = e.AlarmEndTs?.ToDateTime()
        }));
      }
      catch (RpcException) { /* FS instance unavailable — skip */ }
    }

    var sorted = allEvents
      .GroupBy(e => e.EventId)
      .Select(g => g.First())
      .OrderByDescending(e => e.AlarmStartTs)
      .Take((int)pageLimit)
      .ToList();

    var hasMoreResult = hasMore || allEvents.Count > (int)pageLimit;
    return Ok(new RetentionEventsListDto
    {
      HasMore = hasMoreResult,
      Events  = sorted,
      NextBeforeAlarmStart = hasMoreResult && sorted.Count > 0
        ? sorted[^1].AlarmStartTs
        : null,
    });
  }

  [HttpGet("event-playlists")]
  public async Task<IActionResult> GetEventPlaylists([FromQuery] string eventId, CancellationToken ct)
  {
    if (string.IsNullOrWhiteSpace(eventId))
      return BadRequest(new { error = "eventId is required" });

    // Fan-out to all FS instances — event_id is local to the FS that stored it.
    GetRecordingsResponse? clipsResp = null;
    foreach (var fsId in registry.AllFsIds)
    {
      var fsClient = new FsService.FsServiceClient(DaprClient.CreateInvocationInvoker(fsId));
      try
      {
        clipsResp = await fsClient.GetClipsForEventAsync(
          new GetClipsForEventRequest { EventId = eventId }, cancellationToken: ct);
        break;
      }
      catch (RpcException) { /* not on this FS instance — try next */ }
    }

    if (clipsResp is null)
      return NotFound(new { error = "event not found" });

    var streamNames = clipsResp.Recordings
      .Select(r => r.StreamName)
      .Distinct()
      .OrderBy(s => s)
      .ToList();

    var perStream = streamNames.Select(s => new
    {
      streamName  = (string?)s,
      playlistUrl = $"/api/arch/hls/event.m3u8?eventId={Uri.EscapeDataString(eventId)}&streamName={Uri.EscapeDataString(s)}"
    });

    var allEntry = new
    {
      streamName  = (string?)null,
      playlistUrl = $"/api/arch/hls/event.m3u8?eventId={Uri.EscapeDataString(eventId)}"
    };

    var streams = streamNames.Count > 1
      ? perStream.Prepend(allEntry).ToList<object>()
      : perStream.ToList<object>();

    return Ok(new { streams });
  }

  [HttpGet("event-clips")]
  public async Task<IActionResult> GetEventClips([FromQuery] string eventId, CancellationToken ct)
  {
    if (string.IsNullOrWhiteSpace(eventId))
      return BadRequest(new { error = "eventId is required" });

    // Fan-out to all FS instances — event_id is local to the FS that stored it.
    GetRecordingsResponse? clipsResp = null;
    foreach (var fsId in registry.AllFsIds)
    {
      var fsClient = new FsService.FsServiceClient(DaprClient.CreateInvocationInvoker(fsId));
      try
      {
        clipsResp = await fsClient.GetClipsForEventAsync(
          new GetClipsForEventRequest { EventId = eventId }, cancellationToken: ct);
        break;
      }
      catch (RpcException) { /* not on this FS instance — try next */ }
    }

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
    foreach (var fsId in registry.AllFsIds)
    {
      var fsClient = new FsService.FsServiceClient(DaprClient.CreateInvocationInvoker(fsId));
      try
      {
        var resp = await fsClient.GetFsActivityAsync(new GetFsActivityRequest { Limit = lim }, cancellationToken: ct);
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
      catch (RpcException) { /* FS instance unavailable — skip */ }
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
