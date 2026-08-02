using Microsoft.AspNetCore.Mvc;
using web_vms.Authorization;

namespace web_vms;

/// <summary>
/// Authenticated WHEP gateway for live video — see <see cref="WhepProxyService"/> for the actual
/// proxying logic. Browsers never address MediaMTX directly.
/// </summary>
[ApiController]
[Route("api/live/whep")]
public class WhepController(WhepProxyService _proxy, ICameraAccessService _access) : ControllerBase
{
  // Whose session this is. Taken from the validated token, never from the request, exactly as
  // MonitorController does it. Empty when authentication is disabled — then everyone shares one
  // bucket, the same way monitor ownership already behaves standalone.
  private string Owner => User.Identity?.Name ?? string.Empty;

  // The camera id is the first segment of the MediaMTX path, so the check lives here rather than
  // in the generic filter.
  [CameraAccess(CameraAction.ViewLive, Source = CameraIdSource.Action)]
  [HttpPost("offer/{*path}")]
  public async Task<IActionResult> CreateOffer(string path, CancellationToken ct)
  {
    var slash = path?.IndexOf('/') ?? -1;
    var cameraId = slash < 0 ? path : path![..slash];
    if (string.IsNullOrWhiteSpace(cameraId))
      return BadRequest("camera id is required");

    if (!await _access.IsAllowedAsync(User, cameraId, CameraAction.ViewLive, ct))
      return StatusCode(StatusCodes.Status403Forbidden);

    var result = await _proxy.CreateOfferAsync(path!, Request.Body, Owner, ct);
    return await result.WriteTo(this, "/api/live/whep/session/", ct);
  }

  // Ending a session the caller owns; ownership is checked inside the proxy against the token.
  [NoCameraAccess("closes the caller's own WHEP session, identified by an opaque token")]
  [HttpDelete("session/{token}")]
  public async Task<IActionResult> DeleteSession(string token, CancellationToken ct)
  {
    var result = await _proxy.DeleteSessionAsync(token, Owner, ct);
    return result.ToActionResult(this);
  }
}
