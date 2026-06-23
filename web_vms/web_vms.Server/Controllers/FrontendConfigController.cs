using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace web_vms.Controllers;

public class FrontendConfigDto
{
  public string? SquareFrontendOrigin { get; set; }
}

// Relays SQUARE_FRONTEND_ORIGIN (a backend runtime env var, see Program.cs's CORS gate) to the SPA.
// The React client is built into the Docker image statically, so Vite's import.meta.env can't see
// a value that only exists once the container starts — this is the same env var, just exposed
// through REST instead of a build-time constant. Anonymous like AuthController.cs, since the SPA
// needs this before/without a Keycloak token.
[ApiController]
[Route("api/config")]
[AllowAnonymous]
public class FrontendConfigController : ControllerBase
{
  [HttpGet("frontend")]
  public ActionResult<FrontendConfigDto> GetConfig()
  {
    return Ok(new FrontendConfigDto
    {
      SquareFrontendOrigin = Environment.GetEnvironmentVariable("SQUARE_FRONTEND_ORIGIN"),
    });
  }
}
