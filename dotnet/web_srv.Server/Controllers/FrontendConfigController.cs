using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace web_srv.Controllers;

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
  /// <summary>
  /// Whether maps get tiles underneath (through /api/map/tiles). The provider's own URL
  /// stays on the server: the browser only ever talks to the caching proxy.
  /// </summary>
  public bool MapTilesEnabled { get; set; }
  /// <summary>Credit line the tile provider requires to be shown on the map.</summary>
  public string? MapTileAttribution { get; set; }
  /// <summary>
  /// Port of the user administration (multitenant_admin in its standalone mode) on this same host,
  /// which the standalone installs run beside the web UI. Null where users live elsewhere: with
  /// Square, and in dev.
  /// </summary>
  public int? UserAdminPort { get; set; }
}

// Relays SQUARE_FRONTEND_ORIGIN (a backend runtime env var, see Program.cs's CORS gate) to the SPA.
// The React client is built into the Docker image statically, so Vite's import.meta.env can't see
// a value that only exists once the container starts — this is the same env var, just exposed
// through REST instead of a build-time constant. Anonymous like AuthController.cs, since the SPA
// needs this before/without a Keycloak token.
[ApiController]
[Route("api/config")]
[AllowAnonymous]
// Answers before the dependencies do, deliberately: this is what the SPA asks first to find out
// whether the backend is there at all, and it needs nothing from the server-to-node map. Refusing
// it during startup makes the page report an unreachable backend instead of loading.
[AllowNotReady]
public class FrontendConfigController(SquareLinkProbe _square) : ControllerBase
{
  /// <summary>
  /// Single source of truth for the VMS_LIVE_TEST_PLAYER gate: read once, consumed by
  /// WhepTestController, by the startup check in Program.cs that forbids combining it with
  /// Keycloak, and by the SPA so it doesn't mount /test/live against a backend that has it off.
  /// </summary>
  public static readonly bool LiveTestPlayerEnabled = string.Equals(
    Environment.GetEnvironmentVariable("VMS_LIVE_TEST_PLAYER"), "true", StringComparison.OrdinalIgnoreCase);

  // Optional: only the standalone installs set it (see FrontendConfigDto.UserAdminPort). Set but not
  // a port is a misconfiguration, refused rather than hidden.
  private static readonly int? UserAdminPort =
    Environment.GetEnvironmentVariable("MT_ADMIN_PORT") is { Length: > 0 } port
      ? int.TryParse(port, out var value) ? value : throw new InvalidOperationException($"MT_ADMIN_PORT='{port}' is not a port number")
      : null;

  [HttpGet("frontend")]
  public async Task<ActionResult<FrontendConfigDto>> GetConfig(CancellationToken ct)
  {
    return Ok(new FrontendConfigDto
    {
      SquareFrontendOrigin = Environment.GetEnvironmentVariable("SQUARE_FRONTEND_ORIGIN"),
      SquareIntegrated = await _square.IsIntegratedAsync(ct),
      LiveTestPlayer = LiveTestPlayerEnabled,
      MapTilesEnabled = MapTilesController.TileUrlTemplate is not null,
      // Optional, shown under the map as given. Empty with tiles on is a misconfiguration for most
      // providers (OpenStreetMap requires the credit), but not one this service can judge.
      MapTileAttribution = Environment.GetEnvironmentVariable("VMS_MAP_TILE_ATTRIBUTION"),
      UserAdminPort = UserAdminPort,
    });
  }
}
