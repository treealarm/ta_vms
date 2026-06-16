using Microsoft.AspNetCore.Mvc;
using web_vms.Types.Dto;

namespace web_vms;

[ApiController]
[Route("api/media-mtx")]
public class MediaMtxController(ICameraManager _manager) : ControllerBase
{
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
