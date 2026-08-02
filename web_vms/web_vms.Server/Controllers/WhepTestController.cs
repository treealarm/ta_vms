using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace web_vms;

/// <summary>
/// Dev-only, unauthenticated mirror of <see cref="WhepController"/> for testing the MediaMTX
/// WHEP proxy in isolation (e.g. via the /test/live frontend page) without logging in. Only
/// active when VMS_LIVE_TEST_PLAYER=true; every route 404s otherwise, as if it didn't exist.
/// </summary>
[ApiController]
[Route("api/live/whep-test")]
[AllowAnonymous]
public class WhepTestController(WhepProxyService _proxy) : ControllerBase
{
  private static bool Enabled => Controllers.FrontendConfigController.LiveTestPlayerEnabled;

  [HttpPost("offer/{*path}")]
  public async Task<IActionResult> CreateOffer(string path, CancellationToken ct)
  {
    if (!Enabled)
      return NotFound();

    // No identity here by construction (the controller is anonymous), so every test session lands
    // in one shared bucket. Safe only because this controller refuses to coexist with Keycloak —
    // see the startup check in Program.cs.
    var result = await _proxy.CreateOfferAsync(path, Request.Body, string.Empty, ct);
    return await result.WriteTo(this, "/api/live/whep-test/session/", ct);
  }

  [HttpDelete("session/{token}")]
  public async Task<IActionResult> DeleteSession(string token, CancellationToken ct)
  {
    if (!Enabled)
      return NotFound();

    var result = await _proxy.DeleteSessionAsync(token, string.Empty, ct);
    return result.ToActionResult(this);
  }
}
