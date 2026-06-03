using Grpc.Core;
using Microsoft.AspNetCore.Mvc;
using Vms.Cfg;

namespace web_vms;

[ApiController]
[Route("api/cameras")]
public class CameraStateController(CfgService.CfgServiceClient cfgClient) : ControllerBase
{
  private CallOptions Options(CancellationToken ct) =>
    new(deadline: DateTime.UtcNow.AddSeconds(5), cancellationToken: ct);

  [HttpGet("{cameraId}/state")]
  public async Task<IActionResult> GetState(string cameraId, CancellationToken ct)
  {
    try
    {
      var resp = await cfgClient.GetCamStateAsync(
        new GetCamStateRequest { CameraId = cameraId }, Options(ct));
      return Ok(new CamStateDto(resp.Armed));
    }
    catch (RpcException ex) { return StatusCode(502, ex.Status.Detail); }
  }

  [HttpPut("{cameraId}/state")]
  public async Task<IActionResult> SetState(string cameraId, [FromBody] CamStateDto body, CancellationToken ct)
  {
    try
    {
      await cfgClient.SetCamStateAsync(
        new SetCamStateRequest { CameraId = cameraId, Armed = body.Armed }, Options(ct));
      return Ok();
    }
    catch (RpcException ex) { return StatusCode(502, ex.Status.Detail); }
  }
}

public record CamStateDto(bool Armed);
