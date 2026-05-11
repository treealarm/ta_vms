using Grpc.Core;
using Microsoft.AspNetCore.Mvc;
using Vms.Onvif;

namespace web_vms;

[ApiController]
[Route("api/[controller]")]
public class OnvifController(OnvifService.OnvifServiceClient onvifClient) : ControllerBase
{
  private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

  private static CallOptions Options(CancellationToken ct) =>
    new(deadline: DateTime.UtcNow.Add(Timeout), cancellationToken: ct);

  [HttpGet("cameras/{cameraId}/profiles")]
  public async Task<IActionResult> GetProfiles(string cameraId, CancellationToken ct)
  {
    try
    {
      var response = await onvifClient.GetProfilesAsync(
        new CameraRequest { SourceId = cameraId }, Options(ct));

      return Ok(response.Profiles.Select(p => new
      {
        token    = p.Token,
        name     = p.Name,
        width    = p.Width,
        height   = p.Height,
        encoding = p.Encoding
      }));
    }
    catch (RpcException ex) when (ex.StatusCode == Grpc.Core.StatusCode.DeadlineExceeded)
    {
      return StatusCode(504, "Camera did not respond within timeout");
    }
    catch (RpcException ex)
    {
      return StatusCode(502, ex.Status.Detail);
    }
  }
}
