using Grpc.Core;
using Microsoft.AspNetCore.Mvc;
using web_vms.Types.Dto;

namespace web_vms;

[ApiController]
[Route("api/[controller]")]
public class MonitorController(MonitorManager manager) : ControllerBase
{
  // The owner of anything this request touches. Taken from the validated token, never from the
  // body. Empty when KEYCLOAK_URL is unset: authentication is disabled there, so every board
  // lands in one shared bucket — expected standalone behaviour, not a hole.
  private string Owner => User.Identity?.Name ?? string.Empty;

  [HttpGet]
  public async Task<IActionResult> List(CancellationToken ct)
  {
    return Ok(await manager.List(Owner, ct));
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
      var result = await manager.Upsert(dto, Owner, ct);
      return Ok(result);
    }
    catch (RpcException ex) when (ex.StatusCode is Grpc.Core.StatusCode.FailedPrecondition or Grpc.Core.StatusCode.PermissionDenied)
    {
      return BadRequest(ex.Status.Detail);
    }
  }

  [HttpDelete("{id}")]
  public async Task<IActionResult> Remove(string id, CancellationToken ct)
  {
    var removed = await manager.Remove(id, Owner, ct);
    return Ok(new ApiResponse(removed, removed ? "ok" : "not found"));
  }

  [HttpPut("{id}/cameras")]
  public async Task<IActionResult> SetCameras(string id, [FromBody] SetMonitorCamerasBody body, CancellationToken ct)
  {
    var result = await manager.SetCameras(id, body.CameraIds, Owner, ct);
    return Ok(result);
  }

  [HttpPut("{id}/slots/{slotIndex}")]
  public async Task<IActionResult> SetSlot(string id, int slotIndex, [FromBody] SetMonitorSlotBody body, CancellationToken ct)
  {
    if (string.IsNullOrWhiteSpace(body.CameraId))
      return BadRequest("cameraId must not be empty");

    var result = await manager.SetSlot(id, slotIndex, body.CameraId, Owner, ct);
    return Ok(result);
  }

  [HttpDelete("{id}/slots/{slotIndex}")]
  public async Task<IActionResult> ClearSlot(string id, int slotIndex, CancellationToken ct)
  {
    return Ok(await manager.ClearSlot(id, slotIndex, Owner, ct));
  }

  /// <summary>
  /// Hands a copy of an investigation board to another user, who becomes its owner. Sharing is a
  /// copy rather than a grant, so neither side can disturb the other's board afterwards.
  /// </summary>
  [HttpPost("{id}/copy-to")]
  public async Task<IActionResult> CopyTo(string id, [FromBody] CopyMonitorBody body, CancellationToken ct)
  {
    if (string.IsNullOrWhiteSpace(body.Username))
      return BadRequest("username must not be empty");

    try
    {
      return Ok(await manager.CopyTo(id, Owner, body.Username, ct));
    }
    catch (RpcException ex) when (ex.StatusCode is Grpc.Core.StatusCode.FailedPrecondition or Grpc.Core.StatusCode.PermissionDenied)
    {
      return BadRequest(ex.Status.Detail);
    }
  }
}
