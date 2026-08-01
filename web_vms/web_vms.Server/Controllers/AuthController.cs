using KeycloakAdmin;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using web_vms.Authentication;
using web_vms.Types.Dto;
using RoleConstants = web_vms.Authentication.RoleConstants;

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
  /// <param name="includeSelf">
  /// Board sharing must not offer "send to myself", so self is excluded by default; the camera
  /// permissions admin panel needs the full list and asks for it explicitly.
  /// </param>
  /// <param name="withRoles">
  /// Also report whether each user holds the realm admin role. Costs one Keycloak call per user, so
  /// it is opt-in: only the camera permissions panel needs it, to show that grants do not apply to
  /// administrators.
  /// </param>
  [HttpGet("users")]
  [Authorize(Policy = AuthPolicies.Authenticated)]
  public async Task<IActionResult> Users(
    [FromQuery] bool includeSelf = false, [FromQuery] bool withRoles = false)
  {
    if (kcAdmin == null)
    {
      return Ok(Array.Empty<RealmUserDto>());
    }

    var realm = RequireEnv("KEYCLOAK_REALM_NAME");
    var me = User.Identity?.Name;
    try
    {
      var users = await kcAdmin.GetUsersAsync(realm);
      var names = users
        .Select(u => u.UserName)
        .Where(n => !string.IsNullOrWhiteSpace(n) && (includeSelf || n != me))
        .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
        .ToArray();

      if (!withRoles)
        return Ok(names.Select(n => new RealmUserDto { Username = n }).ToArray());

      var result = new List<RealmUserDto>(names.Length);
      foreach (var name in names)
      {
        bool? isAdmin;
        try
        {
          var roles = await kcAdmin.GetUserRolesAsync(realm, name);
          isAdmin = roles.Any(r => string.Equals(r.Name, RoleConstants.Admin, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception)
        {
          // One unreadable user must not blank the whole list; unknown is reported as unknown.
          isAdmin = null;
        }
        result.Add(new RealmUserDto { Username = name, IsAdmin = isAdmin });
      }
      return Ok(result);
    }
    catch (Exception)
    {
      return Ok(Array.Empty<RealmUserDto>());
    }
  }

  /// <summary>
  /// What the caller may do, so the SPA can hide controls it would only get a 403 from. Purely
  /// advisory: every endpoint checks permissions on its own regardless of what this returned.
  /// </summary>
  [HttpGet("me")]
  [Authorize(Policy = AuthPolicies.Authenticated)]
  public async Task<ActionResult<MyPermissionsDto>> Me(
    [FromServices] IPermissionManager permissions, CancellationToken ct)
  {
    var username = User.Identity?.Name ?? string.Empty;
    // With authentication off there is no identity to scope anything to, and every policy already
    // succeeds — report it as unrestricted rather than as an anonymous user with no grants.
    var isAdmin = !AuthGate.KeycloakEnabled || User.IsInRole(web_vms.Authentication.RoleConstants.Admin);
    var enforced = AuthGate.KeycloakEnabled && await permissions.GetAclEnforced(ct);

    if (!enforced || isAdmin)
      return Ok(new MyPermissionsDto { Username = username, IsAdmin = isAdmin, AclEnforced = enforced });

    var mine = await permissions.GetForUser(username, ct);
    return Ok(new MyPermissionsDto
    {
      Username    = username,
      IsAdmin     = false,
      AclEnforced = true,
      AllCameras  = mine.AllCameras,
      Cameras     = mine.Cameras,
    });
  }
}
