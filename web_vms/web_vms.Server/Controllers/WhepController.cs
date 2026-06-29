using Microsoft.AspNetCore.Mvc;

namespace web_vms;

/// <summary>
/// Authenticated WHEP gateway for live video — see <see cref="WhepProxyService"/> for the actual
/// proxying logic. Browsers never address MediaMTX directly.
/// </summary>
[ApiController]
[Route("api/live/whep")]
public class WhepController(WhepProxyService _proxy) : ControllerBase
{
  [HttpPost("offer/{*path}")]
  public async Task<IActionResult> CreateOffer(string path, CancellationToken ct)
  {
    var result = await _proxy.CreateOfferAsync(path, Request.Body, ct);
    return await result.WriteTo(this, "/api/live/whep/session/", ct);
  }

  [HttpDelete("session/{token}")]
  public async Task<IActionResult> DeleteSession(string token, CancellationToken ct)
  {
    var result = await _proxy.DeleteSessionAsync(token, ct);
    return result.ToActionResult(this);
  }
}
