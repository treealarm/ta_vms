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
    if (string.IsNullOrWhiteSpace(dto.RtspUrl))
      return BadRequest("rtspUrl must not be empty");

    var response = await _cfgClient.UpsertAnalyticsWatchAsync(
      new UpsertAnalyticsWatchRequest { Watch = ToProto(dto) }, CfgOptions(ct));
    return Ok(new ApiResponse(response.Success, response.Message));
  }

  [HttpDelete("{id}")]
  public async Task<IActionResult> Remove(string id, CancellationToken ct)
  {
    var response = await _cfgClient.RemoveAnalyticsWatchAsync(
      new RemoveAnalyticsWatchRequest { Id = id }, CfgOptions(ct));
    return Ok(new ApiResponse(response.Success, response.Message));
  }

  private static AnalyticsWatchDto ToDto(AnalyticsWatch w) => new()
  {
    Id             = w.Id,
    RtspUrl        = w.RtspUrl,
    CredUser       = w.HasCredUser ? w.CredUser : null,
    CredPass       = w.HasCredPass ? w.CredPass : null,
    Enabled        = w.Enabled,
    Classes        = [.. w.Classes],
    MinConfidence  = w.MinConfidence,
    SampleFps      = w.SampleFps,
    Cameras        = [.. w.Cameras.Select(c => new AnalyticsWatchCameraLinkDto { CameraId = c.CameraId, Armed = c.Armed })],
  };

  private static AnalyticsWatch ToProto(AnalyticsWatchDto dto)
  {
    var watch = new AnalyticsWatch
    {
      Id            = dto.Id ?? string.Empty,
      RtspUrl       = dto.RtspUrl,
      Enabled       = dto.Enabled,
      MinConfidence = dto.MinConfidence,
      SampleFps     = dto.SampleFps,
    };
    if (dto.CredUser != null) watch.CredUser = dto.CredUser;
    if (dto.CredPass != null) watch.CredPass = dto.CredPass;
    watch.Classes.AddRange(dto.Classes);
    watch.Cameras.AddRange(dto.Cameras.Select(c => new AnalyticsWatchCameraLink { CameraId = c.CameraId }));
    return watch;
  }
}
