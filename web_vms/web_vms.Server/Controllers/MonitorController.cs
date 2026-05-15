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
    try
    {
      return Ok(await manager.List(ct));
    }
    catch (RpcException ex)
    {
      return StatusCode(503, new ApiResponse(false, $"Config service error: {ex.Status.Detail}"));
    }
  }

  [HttpPost]
  public async Task<IActionResult> Upsert([FromBody] MonitorDto dto, CancellationToken ct)
  {
    if (string.IsNullOrWhiteSpace(dto.Name))
      return BadRequest("name must not be empty");

    if (string.IsNullOrWhiteSpace(dto.Layout))
      return BadRequest("layout must not be empty");

    try
    {
      var result = await manager.Upsert(dto, ct);
      return Ok(result);
    }
    catch (RpcException ex)
    {
      return StatusCode(503, new ApiResponse(false, $"Config service error: {ex.Status.Detail}"));
    }
  }

  [HttpDelete("{id}")]
  public async Task<IActionResult> Remove(string id, CancellationToken ct)
  {
    try
    {
      var removed = await manager.Remove(id, ct);
      return Ok(new ApiResponse(removed, removed ? "ok" : "not found"));
    }
    catch (RpcException ex)
    {
      return StatusCode(503, new ApiResponse(false, $"Config service error: {ex.Status.Detail}"));
    }
  }

  [HttpPut("{id}/cameras")]
  public async Task<IActionResult> SetCameras(string id, [FromBody] SetMonitorCamerasBody body, CancellationToken ct)
  {
    try
    {
      await manager.SetCameras(id, body.CameraIds, ct);
      return Ok(new ApiResponse(true, "ok"));
    }
    catch (RpcException ex)
    {
      return StatusCode(503, new ApiResponse(false, $"Config service error: {ex.Status.Detail}"));
    }
  }

  [HttpPut("{id}/slots/{slotIndex}")]
  public async Task<IActionResult> SetSlot(string id, int slotIndex, [FromBody] SetMonitorSlotBody body, CancellationToken ct)
  {
    if (string.IsNullOrWhiteSpace(body.CameraId))
      return BadRequest("cameraId must not be empty");

    try
    {
      await manager.SetSlot(id, slotIndex, body.CameraId, ct);
      return Ok(new ApiResponse(true, "ok"));
    }
    catch (RpcException ex)
    {
      return StatusCode(503, new ApiResponse(false, $"Config service error: {ex.Status.Detail}"));
    }
  }
}
