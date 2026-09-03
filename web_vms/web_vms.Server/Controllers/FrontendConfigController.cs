using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace web_vms.Controllers;

public class FrontendConfigDto
{
  public string? SquareFrontendOrigin { get; set; }
  /// <summary>
  /// This deployment really is integrated with Square: the integration is switched on and its
  /// startup resync has reached Square at least once. Knowing where Square's UI lives
  /// (<see cref="SquareFrontendOrigin"/>) is a separate question — that value also gates CORS, and
  /// is set in dev whether or not a Square is running.
  /// </summary>
  public bool SquareIntegrated { get; set; }
  /// <summary>Whether the dev-only anonymous WHEP test page is served at all.</summary>
  public bool LiveTestPlayer { get; set; }
}

// Relays SQUARE_FRONTEND_ORIGIN (a backend runtime env var, see Program.cs's CORS gate) to the SPA.
// The React client is built into the Docker image statically, so Vite's import.meta.env can't see
// a value that only exists once the container starts — this is the same env var, just exposed
// through REST instead of a build-time constant. Anonymous like AuthController.cs, since the SPA
// needs this before/without a Keycloak token.
[ApiController]
[Route("api/config")]
[AllowAnonymous]
public class FrontendConfigController(SquareLinkProbe _square) : ControllerBase
{
  /// <summary>
  /// Single source of truth for the VMS_LIVE_TEST_PLAYER gate: read once, consumed by
  /// WhepTestController, by the startup check in Program.cs that forbids combining it with
  /// Keycloak, and by the SPA so it doesn't mount /test/live against a backend that has it off.
  /// </summary>
  public static readonly bool LiveTestPlayerEnabled = string.Equals(
    Environment.GetEnvironmentVariable("VMS_LIVE_TEST_PLAYER"), "true", StringComparison.OrdinalIgnoreCase);

  [HttpGet("frontend")]
  public async Task<ActionResult<FrontendConfigDto>> GetConfig(CancellationToken ct)
  {
    return Ok(new FrontendConfigDto
    {
      SquareFrontendOrigin = Environment.GetEnvironmentVariable("SQUARE_FRONTEND_ORIGIN"),
      SquareIntegrated = await _square.IsIntegratedAsync(ct),
      LiveTestPlayer = LiveTestPlayerEnabled,
    });
  }
}
