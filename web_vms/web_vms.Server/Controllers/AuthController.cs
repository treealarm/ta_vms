using KeycloakAdmin;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

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
public class AuthController(IKeycloakAdminClient kcAdmin) : ControllerBase
{
  private static string RequireEnv(string name) =>
    Environment.GetEnvironmentVariable(name)
      ?? throw new InvalidOperationException($"Required environment variable '{name}' is not set");

  [HttpPost("login")]
  public async Task<IActionResult> Login(LoginDto dto)
  {
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
    var realm = RequireEnv("KEYCLOAK_REALM_NAME");
    var clientId = RequireEnv("KEYCLOAK_CLIENT_ID");

    var token = await kcAdmin.RefreshAccessToken(realm, clientId, dto.RefreshToken);
    if (token == null)
    {
      return Unauthorized();
    }

    return Ok(token);
  }
}
