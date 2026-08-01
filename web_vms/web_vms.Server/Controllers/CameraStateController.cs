using Grpc.Core;
using Microsoft.AspNetCore.Mvc;
using Vms.Cfg;
using web_vms.Authorization;

namespace web_vms;

[ApiController]
[Route("api/cameras")]
public class CameraStateController(CfgService.CfgServiceClient cfgClient) : ControllerBase
{
  private CallOptions Options(CancellationToken ct) =>
    new(deadline: DateTime.UtcNow.AddSeconds(5), cancellationToken: ct);

  [HttpGet("{cameraId}/state")]
  [CameraAccess(CameraAction.ViewLive)]
  public async Task<IActionResult> GetState(string cameraId, CancellationToken ct)
  {
    var resp = await cfgClient.GetCamStateAsync(
      new GetCamStateRequest { CameraId = cameraId }, Options(ct));
    return Ok(new CamStateDto(resp.Armed));
  }

  // Arming is a control action, not a viewing one.
  [HttpPut("{cameraId}/state")]
  [CameraAccess(CameraAction.Control)]
  public async Task<IActionResult> SetState(string cameraId, [FromBody] CamStateDto body, CancellationToken ct)
  {
    await cfgClient.SetCamStateAsync(
      new SetCamStateRequest { CameraId = cameraId, Armed = body.Armed }, Options(ct));
    return Ok();
  }
}

public record CamStateDto(bool Armed);
