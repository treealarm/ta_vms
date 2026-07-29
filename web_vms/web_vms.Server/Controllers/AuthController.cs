using KeycloakAdmin;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using web_vms.Authentication;

namespace web_vms.Controllers;

public class LoginDto
{
  public string Username { get; set; } = "";
  public string Password { get; set; } = "";
}

public class RefreshTokenDto
{
  public string RefreshToken { get; set; } = "";
}

[ApiController]
[Route("api/auth")]
[AllowAnonymous]
// IKeycloakAdminClient is only registered when KEYCLOAK_URL is set (see Program.cs) — kcAdmin is
// optional here so standalone deployments get a clean response instead of a DI-resolution crash.
public class AuthController(IKeycloakAdminClient? kcAdmin = null) : ControllerBase
{
  private static string RequireEnv(string name) =>
    Environment.GetEnvironmentVariable(name)
      ?? throw new InvalidOperationException($"Required environment variable '{name}' is not set");

  [HttpPost("login")]
  public async Task<IActionResult> Login(LoginDto dto)
  {
    if (kcAdmin == null)
    {
      return StatusCode(StatusCodes.Status503ServiceUnavailable, "Authentication is not enabled on this server");
    }

    var realm = RequireEnv("KEYCLOAK_REALM_NAME");
    var clientId = RequireEnv("KEYCLOAK_CLIENT_ID");

    try
    {
      var token = await kcAdmin.GetTokenAsync(realm, clientId, dto.Username, dto.Password);
      return Ok(token);
    }
    catch (Exception)
    {
      return Unauthorized();
    }
  }

  [HttpPost("refresh")]
  public async Task<IActionResult> Refresh(RefreshTokenDto dto)
  {
    if (kcAdmin == null)
    {
      return StatusCode(StatusCodes.Status503ServiceUnavailable, "Authentication is not enabled on this server");
    }

    var realm = RequireEnv("KEYCLOAK_REALM_NAME");
    var clientId = RequireEnv("KEYCLOAK_CLIENT_ID");

    try
    {
      var token = await kcAdmin.RefreshAccessToken(realm, clientId, dto.RefreshToken);
      if (token == null)
      {
        return Unauthorized();
      }

      return Ok(token);
    }
    catch (Exception)
    {
      return Unauthorized();
    }
  }

  /// <summary>
  /// Usernames in the realm, for picking who to send a copy of an investigation board to.
  /// Empty when Keycloak isn't configured — there are no users to name, and the UI hides sharing.
  /// </summary>
  [HttpGet("users")]
  [Authorize(Policy = AuthPolicies.Authenticated)]
  public async Task<IActionResult> Users()
  {
    if (kcAdmin == null)
    {
      return Ok(Array.Empty<string>());
    }

    var realm = RequireEnv("KEYCLOAK_REALM_NAME");
    var me = User.Identity?.Name;
    try
    {
      var users = await kcAdmin.GetUsersAsync(realm);
      var names = users
        .Select(u => u.UserName)
        .Where(n => !string.IsNullOrWhiteSpace(n) && n != me)
        .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
        .ToArray();
      return Ok(names);
    }
    catch (Exception)
    {
      return Ok(Array.Empty<string>());
    }
  }
}
