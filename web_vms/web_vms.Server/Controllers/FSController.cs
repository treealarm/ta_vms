using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Mvc;
using Vms.Fs;
using web_vms;
using web_vms.Types.Dto;

namespace web_vms.Server.Controllers;

[Route("api/[controller]")]
[ApiController]
public sealed class FSController(
  ICameraManager manager,
  FsService.FsServiceClient fsClient) : ControllerBase
{
  private static DateTime ToUtc(DateTime dt) =>
    dt.Kind switch
    {
      DateTimeKind.Utc => dt,
      DateTimeKind.Local => dt.ToUniversalTime(),
      _ => DateTime.SpecifyKind(dt, DateTimeKind.Utc)
    };

  private static RecordingDto ToRecordingDto(Vms.Fs.Recording r) => new()
  {
    FilePath = r.FilePath,
    StartTime = r.StartTs.ToDateTime()
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

    GetRecordingsResponse grpcResp;
    if (query.Timestamp.HasValue)
    {
      grpcResp = await fsClient.GetRecordingsAroundAsync(new GetRecordingsAroundRequest
      {
        CameraId = query.CameraId,
        Timestamp = Timestamp.FromDateTime(ToUtc(query.Timestamp.Value)),
        Limit = limit
      }, cancellationToken: ct);
    }
    else
    {
      var req = new GetRecordingsFromToRequest
      {
        CameraId = query.CameraId,
        Limit = limit
      };
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

    if (await manager.Get(query.CameraId) is null)
      return NotFound();

    var grpcReq = new GetRecordingRangesRequest
    {
      CameraId = query.CameraId,
      Limit = query.Limit ?? 50,
      GapToleranceMs = 1000
    };

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
        FilePath = r.FilePath,
        StartTime = r.StartTs.ToDateTime(),
        EndTime = r.EndTs?.ToDateTime() ?? r.StartTs.ToDateTime(),
        ClipsCount = r.ClipsCount,
        EventIds = r.EventIds.ToList()
      }).ToList()
    };

    return Ok(dto);
  }

  [HttpPost("events")]
  public async Task<IActionResult> RetainEvent([FromBody] RetentionEventDto body, CancellationToken ct)
  {
    if (string.IsNullOrWhiteSpace(body.CameraId))
      return BadRequest(new { error = "cameraId is required" });

    if (await manager.Get(body.CameraId) is null)
      return NotFound();

    var grpcReq = new RetainEventRequest
    {
      CameraId = body.CameraId,
      AlarmStartTs = Timestamp.FromDateTime(ToUtc(body.AlarmStartTs))
    };

    if (!string.IsNullOrWhiteSpace(body.EventId))
      grpcReq.EventId = body.EventId!;
    if (body.AlarmEndTs.HasValue)
      grpcReq.AlarmEndTs = Timestamp.FromDateTime(ToUtc(body.AlarmEndTs.Value));

    var resp = await fsClient.RetainEventAsync(grpcReq, cancellationToken: ct);
    if (resp.Status != OperationStatus.Types.Status.Success)
      return StatusCode(500, new { success = false, message = resp.Message });

    return Ok(new ApiResponse(true, "ok"));
  }

  [HttpGet("retention-events")]
  public async Task<IActionResult> ListRetentionEvents([FromQuery] int? limit, [FromQuery] string? before, CancellationToken ct)
  {
    var grpcReq = new ListRetentionEventsRequest { Limit = (uint)(limit ?? 50) };
    if (!string.IsNullOrWhiteSpace(before))
    {
      if (!DateTime.TryParse(before, null, System.Globalization.DateTimeStyles.RoundtripKind, out var bt))
        return BadRequest(new { error = "before must be RFC3339" });
      grpcReq.BeforeAlarmStartTs = Timestamp.FromDateTime(ToUtc(bt));
    }

    var grpcResp = await fsClient.ListRetentionEventsAsync(grpcReq, cancellationToken: ct);

    var dto = new RetentionEventsListDto
    {
      HasMore = grpcResp.HasMore,
      Events = grpcResp.Events.Select(e => new RetentionEventRowDto
      {
        EventId = e.EventId,
        CameraId = e.CameraId,
        AlarmStartTs = e.AlarmStartTs.ToDateTime(),
        AlarmEndTs = e.AlarmEndTs?.ToDateTime()
      }).ToList()
    };

    if (grpcResp.NextBeforeAlarmStartTs != null)
      dto.NextBeforeAlarmStart = grpcResp.NextBeforeAlarmStartTs.ToDateTime();

    return Ok(dto);
  }
}
