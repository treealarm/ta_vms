using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using web_srv.Authentication;
using web_srv.Types.Dto;

namespace web_srv;

[ApiController]
[Route("api/media-mtx")]
[Authorize(Policy = AuthPolicies.AdminOnly)]
public partial class MediaMtxController(ICameraManager _manager) : ControllerBase
{
  [GeneratedRegex(@"^stuns?:\S+$")]
  private static partial Regex StunUrlPattern();

  [HttpGet("servers")]
  public async Task<IActionResult> ListServers(CancellationToken ct)
  {
    return Ok(await _manager.ListMediaMtxServers(ct));
  }

  [HttpPut("servers")]
  public async Task<IActionResult> UpsertServer([FromBody] MediaMtxServerDto dto, CancellationToken ct)
  {
    if (string.IsNullOrWhiteSpace(dto.Name))
      return BadRequest("name must not be empty");

    if (string.IsNullOrWhiteSpace(dto.Host))
      return BadRequest("host must not be empty");

    // Empty means "no STUN, LAN only". Anything else has to be a URL the browser can parse:
    // RTCPeerConnection throws on a malformed one, killing live playback for every camera on
    // this router. turn: is rejected too — it needs credentials this config does not carry.
    if (!string.IsNullOrWhiteSpace(dto.StunUrl) && !StunUrlPattern().IsMatch(dto.StunUrl.Trim()))
      return BadRequest("stunUrl must be a stun: or stuns: URL, e.g. stun:stun.example.org:19302");

    var ok = await _manager.UpsertMediaMtxServer(dto, ct);
    return Ok(new ApiResponse(ok, ok ? "ok" : "failed"));
  }

  [HttpDelete("servers/{id}")]
  public async Task<IActionResult> RemoveServer(string id, CancellationToken ct)
  {
    if (string.IsNullOrWhiteSpace(id))
      return BadRequest("id must not be empty");

    var ok = await _manager.RemoveMediaMtxServer(id, ct);
    return Ok(new ApiResponse(ok, ok ? "ok" : "not found"));
  }
}
