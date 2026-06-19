using Grpc.Core;
using Microsoft.AspNetCore.Mvc;
using web_vms.Types.Dto;

namespace web_vms;

[ApiController]
[Route("api/[controller]")]
public class MonitorController(MonitorManager manager) : ControllerBase
{
  [HttpGet]
  public async Task<IActionResult> List(CancellationToken ct)
  {
    return Ok(await manager.List(ct));
  }

  [HttpPost]
  public async Task<IActionResult> Upsert([FromBody] MonitorDto dto, CancellationToken ct)
  {
    if (string.IsNullOrWhiteSpace(dto.Name))
      return BadRequest("name must not be empty");

    if (string.IsNullOrWhiteSpace(dto.Layout))
      return BadRequest("layout must not be empty");

    var result = await manager.Upsert(dto, ct);
    return Ok(result);
  }

  [HttpDelete("{id}")]
  public async Task<IActionResult> Remove(string id, CancellationToken ct)
  {
    var removed = await manager.Remove(id, ct);
    return Ok(new ApiResponse(removed, removed ? "ok" : "not found"));
  }

  [HttpPut("{id}/cameras")]
  public async Task<IActionResult> SetCameras(string id, [FromBody] SetMonitorCamerasBody body, CancellationToken ct)
  {
    var result = await manager.SetCameras(id, body.CameraIds, ct);
    return Ok(result);
  }

  [HttpPut("{id}/slots/{slotIndex}")]
  public async Task<IActionResult> SetSlot(string id, int slotIndex, [FromBody] SetMonitorSlotBody body, CancellationToken ct)
  {
    if (string.IsNullOrWhiteSpace(body.CameraId))
      return BadRequest("cameraId must not be empty");

    var result = await manager.SetSlot(id, slotIndex, body.CameraId, ct);
    return Ok(result);
  }
}
