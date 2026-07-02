using Grpc.Core;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Vms.Cfg;
using web_vms.Authentication;
using web_vms.Types.Dto;

namespace web_vms;

[ApiController]
[Route("api/analytics-watches")]
[Authorize(Policy = AuthPolicies.AdminOnly)]
public class AnalyticsWatchController(CfgService.CfgServiceClient _cfgClient) : ControllerBase
{
  private static readonly TimeSpan CfgCallTimeout = TimeSpan.FromSeconds(5);

  private static CallOptions CfgOptions(CancellationToken ct) =>
    new(deadline: DateTime.UtcNow.Add(CfgCallTimeout), cancellationToken: ct);

  [HttpGet]
  public async Task<IActionResult> List(CancellationToken ct)
  {
    var response = await _cfgClient.ListAnalyticsWatchesAsync(new ListAnalyticsWatchesRequest(), CfgOptions(ct));
    return Ok(response.Watches.Select(ToDto));
  }

  [HttpPut]
  public async Task<IActionResult> Upsert([FromBody] AnalyticsWatchDto dto, CancellationToken ct)
  {
    if (string.IsNullOrWhiteSpace(dto.CameraId))
      return BadRequest("cameraId must not be empty");
    if (string.IsNullOrWhiteSpace(dto.StreamName))
      return BadRequest("streamName must not be empty");

    var response = await _cfgClient.UpsertAnalyticsWatchAsync(
      new UpsertAnalyticsWatchRequest { Watch = ToProto(dto) }, CfgOptions(ct));
    return Ok(new ApiResponse(response.Success, response.Message));
  }

  [HttpDelete("{cameraId}")]
  public async Task<IActionResult> Remove(string cameraId, CancellationToken ct)
  {
    var response = await _cfgClient.RemoveAnalyticsWatchAsync(
      new RemoveAnalyticsWatchRequest { CameraId = cameraId }, CfgOptions(ct));
    return Ok(new ApiResponse(response.Success, response.Message));
  }

  private static AnalyticsWatchDto ToDto(AnalyticsWatch w) => new()
  {
    CameraId      = w.CameraId,
    StreamName    = w.StreamName,
    Enabled       = w.Enabled,
    Classes       = [.. w.Classes],
    MinConfidence = w.MinConfidence,
    SampleFps     = w.SampleFps,
    Armed         = w.Armed,
  };

  private static AnalyticsWatch ToProto(AnalyticsWatchDto dto)
  {
    var watch = new AnalyticsWatch
    {
      CameraId      = dto.CameraId,
      StreamName    = dto.StreamName,
      Enabled       = dto.Enabled,
      MinConfidence = dto.MinConfidence,
      SampleFps     = dto.SampleFps,
    };
    watch.Classes.AddRange(dto.Classes);
    return watch;
  }
}
