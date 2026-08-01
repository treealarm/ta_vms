using Grpc.Core;
using Microsoft.AspNetCore.Mvc;
using web_vms.Types.Dto;
using web_vms.Authorization;

namespace web_vms;

[ApiController]
[Route("api/[controller]")]
public class MonitorController(MonitorManager manager, ICameraAccessService access) : ControllerBase
{
  // The owner of anything this request touches. Taken from the validated token, never from the
  // body. Empty when KEYCLOAK_URL is unset: authentication is disabled there, so every board
  // lands in one shared bucket — expected standalone behaviour, not a hole.
  private string Owner => User.Identity?.Name ?? string.Empty;

  /// <summary>
  /// Walls are shared, so one can name cameras a given operator may not view. The board itself is
  /// still returned — its existence is not sensitive and a vanishing wall is more confusing than an
  /// empty one — but its camera contents are trimmed to what the caller may see.
  /// </summary>
  [CameraAccess(CameraAction.ViewLive, Source = CameraIdSource.Action)]
  [HttpGet]
  public async Task<IActionResult> List(CancellationToken ct)
  {
    var monitors = await manager.List(Owner, ct);
    var scope = await access.ScopeAsync(User, CameraAction.ViewLive, ct);
    if (scope.All)
      return Ok(monitors);

    return Ok(monitors.Select(m => m with
    {
      CameraIds = [.. m.CameraIds.Where(scope.Contains)],
      Slots     = [.. m.Slots.Where(slot => scope.Contains(slot.CameraId))],
    }).ToList());
  }

  [NoCameraAccess("board metadata (name/layout/kind); camera contents are set through the endpoints below")]
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
    catch (RpcException ex) when (ex.StatusCode is Grpc.Core.StatusCode.FailedPrecondition
                                                or Grpc.Core.StatusCode.PermissionDenied
                                                or Grpc.Core.StatusCode.AlreadyExists)
    {
      return BadRequest(ex.Status.Detail);
    }
  }

  [NoCameraAccess("board removal is governed by board ownership, enforced in VmsCfg SQL")]
  [HttpDelete("{id}")]
  public async Task<IActionResult> Remove(string id, CancellationToken ct)
  {
    var removed = await manager.Remove(id, Owner, ct);
    return Ok(new ApiResponse(removed, removed ? "ok" : "not found"));
  }

  [CameraAccess(CameraAction.ViewLive, Source = CameraIdSource.Action)]
  [HttpPut("{id}/cameras")]
  public async Task<IActionResult> SetCameras(string id, [FromBody] SetMonitorCamerasBody body, CancellationToken ct)
  {
    // Refuse rather than silently drop: the caller asked for a specific set, and a board that
    // quietly lost cameras would look like data loss.
    var scope = await access.ScopeAsync(User, CameraAction.ViewLive, ct);
    if (body.CameraIds.Any(id => !scope.Contains(id)))
      return StatusCode(StatusCodes.Status403Forbidden, "the board names a camera you cannot view");

    var result = await manager.SetCameras(id, body.CameraIds, Owner, ct);
    return Ok(result);
  }

  [CameraAccess(CameraAction.ViewLive, Source = CameraIdSource.Action)]
  [HttpPut("{id}/slots/{slotIndex}")]
  public async Task<IActionResult> SetSlot(string id, int slotIndex, [FromBody] SetMonitorSlotBody body, CancellationToken ct)
  {
    if (string.IsNullOrWhiteSpace(body.CameraId))
      return BadRequest("cameraId must not be empty");

    if (!await access.IsAllowedAsync(User, body.CameraId, CameraAction.ViewLive, ct))
      return StatusCode(StatusCodes.Status403Forbidden);

    var result = await manager.SetSlot(id, slotIndex, body.CameraId, Owner, ct);
    return Ok(result);
  }

  [NoCameraAccess("clearing a slot removes a camera from a board, it never grants access")]
  [HttpDelete("{id}/slots/{slotIndex}")]
  public async Task<IActionResult> ClearSlot(string id, int slotIndex, CancellationToken ct)
  {
    return Ok(await manager.ClearSlot(id, slotIndex, Owner, ct));
  }

  /// <summary>
  /// Hands a copy of an investigation board to another user, who becomes its owner. Sharing is a
  /// copy rather than a grant, so neither side can disturb the other's board afterwards.
  /// </summary>
  [NoCameraAccess("copies a board the caller already owns; the copy shows only what its recipient may view")]
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
