using Grpc.Core;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Vms.Cfg;
using web_srv.Authentication;
using web_srv.Types.Dto;

namespace web_srv;

[ApiController]
[Route("api/recording-schedules")]
[Authorize(Policy = AuthPolicies.AdminOnly)]
public class RecordingScheduleController(
  CfgService.CfgServiceClient _cfgClient) : ControllerBase
{
  private static readonly TimeSpan CfgCallTimeout = TimeSpan.FromSeconds(5);

  private static CallOptions CfgOptions(CancellationToken ct) =>
    new(deadline: DateTime.UtcNow.Add(CfgCallTimeout), cancellationToken: ct);

  [HttpGet]
  public async Task<IActionResult> List(CancellationToken ct)
  {
    var response = await _cfgClient.ListRecordingSchedulesAsync(
      new ListRecordingSchedulesRequest(), CfgOptions(ct));
    return Ok(response.Schedules.Select(ToDto));
  }

  [HttpPut]
  public async Task<IActionResult> Upsert([FromBody] RecordingScheduleDto dto, CancellationToken ct)
  {
    if (string.IsNullOrWhiteSpace(dto.Name))
      return BadRequest("name must not be empty");

    var response = await _cfgClient.UpsertRecordingScheduleAsync(ToProto(dto), CfgOptions(ct));
    return Ok(new ApiResponse(response.Success, response.Message));
  }

  [HttpDelete("{id}")]
  public async Task<IActionResult> Remove(string id, CancellationToken ct)
  {
    var response = await _cfgClient.RemoveRecordingScheduleAsync(
      new RecordingScheduleRequest { Id = id }, CfgOptions(ct));
    return Ok(new ApiResponse(response.Success, response.Message));
  }

  [HttpPost("cameras/{cameraId}")]
  public async Task<IActionResult> SetCameraSchedules(
    string cameraId,
    [FromBody] CameraSchedulesDto dto,
    CancellationToken ct)
  {
    if (string.IsNullOrWhiteSpace(cameraId))
      return BadRequest("cameraId must not be empty");

    var req = new SetCameraSchedulesRequest { CameraId = cameraId };
    req.ScheduleIds.AddRange(dto.ScheduleIds);

    var response = await _cfgClient.SetCameraSchedulesAsync(req, CfgOptions(ct));
    return Ok(new ApiResponse(response.Success, response.Message));
  }

  private static RecordingScheduleDto ToDto(RecordingSchedule s) => new()
  {
    Id            = s.Id,
    Name          = s.Name,
    Timezone      = s.Timezone,
    LeadInSeconds = s.LeadInSeconds,
    Windows       = s.Windows.Select(w => new ScheduleWindowDto
    {
      Kind        = w.Kind == ScheduleWindowKind.ScheduleWindowDate ? "date" : "weekly",
      DayOfWeek   = w.DayOfWeek,
      Date        = w.Date,
      StartSecond = w.StartSecond,
      EndSecond   = w.EndSecond,
    }).ToList(),
  };

  private static RecordingSchedule ToProto(RecordingScheduleDto dto)
  {
    var proto = new RecordingSchedule
    {
      Id            = dto.Id ?? string.Empty,
      Name          = dto.Name,
      Timezone      = string.IsNullOrWhiteSpace(dto.Timezone) ? "UTC" : dto.Timezone,
      LeadInSeconds = dto.LeadInSeconds,
    };

    foreach (var w in dto.Windows)
    {
      proto.Windows.Add(new ScheduleWindow
      {
        Kind        = string.Equals(w.Kind, "date", StringComparison.OrdinalIgnoreCase)
                        ? ScheduleWindowKind.ScheduleWindowDate
                        : ScheduleWindowKind.ScheduleWindowWeekly,
        DayOfWeek   = w.DayOfWeek,
        Date        = w.Date,
        StartSecond = w.StartSecond,
        EndSecond   = w.EndSecond,
      });
    }

    return proto;
  }
}
