using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using web_srv.Authentication;
using web_srv.Types.Dto;

namespace web_srv;

/// <summary>
/// Admin surface for per-camera user permissions. Users themselves come from Keycloak
/// (<c>GET /api/auth/users</c>) — this only stores what each of them may reach.
/// </summary>
[ApiController]
[Route("api/permissions")]
[Authorize(Policy = AuthPolicies.AdminOnly)]
public class CameraPermissionsController(IPermissionManager _permissions) : ControllerBase
{
  [HttpGet("cameras")]
  public async Task<IActionResult> ListAll(CancellationToken ct)
  {
    return Ok(await _permissions.ListAll(ct));
  }

  [HttpGet("cameras/{username}")]
  public async Task<IActionResult> GetForUser(string username, CancellationToken ct)
  {
    if (string.IsNullOrWhiteSpace(username))
      return BadRequest("username must not be empty");

    return Ok(await _permissions.GetForUser(username, ct));
  }

  /// <summary>Full replacement — the UI edits the whole matrix and applies it in one go.</summary>
  [HttpPut("cameras/{username}")]
  public async Task<IActionResult> ReplaceForUser(
    string username, [FromBody] UserCameraPermissionsDto dto, CancellationToken ct)
  {
    if (string.IsNullOrWhiteSpace(username))
      return BadRequest("username must not be empty");

    // The route names the subject; a body naming someone else is a mistake worth refusing rather
    // than silently resolving one way or the other.
    if (!string.IsNullOrWhiteSpace(dto.Username) && dto.Username != username)
      return BadRequest("username in the body does not match the route");

    var ok = await _permissions.ReplaceForUser(dto with { Username = username }, ct);
    return Ok(new ApiResponse(ok, ok ? "ok" : "failed"));
  }

  [HttpGet("mode")]
  public async Task<IActionResult> GetMode(CancellationToken ct)
  {
    return Ok(new CameraAclModeDto { Enforced = await _permissions.GetAclEnforced(ct) });
  }

  /// <summary>
  /// Turning enforcement on takes effect immediately for everyone: users with no grants lose access
  /// to every camera at once. The UI warns before calling this.
  /// </summary>
  [HttpPut("mode")]
  public async Task<IActionResult> SetMode([FromBody] CameraAclModeDto dto, CancellationToken ct)
  {
    var ok = await _permissions.SetAclEnforced(dto.Enforced, ct);
    return Ok(new ApiResponse(ok, ok ? "ok" : "failed"));
  }
}
