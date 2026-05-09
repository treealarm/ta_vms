using Microsoft.AspNetCore.Mvc;
using Vms.Cfg;

namespace web_vms.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class StorageController(StorageService.StorageServiceClient storageClient) : ControllerBase
{
  [HttpGet]
  public async Task<IActionResult> ListStorages(CancellationToken ct)
  {
    var resp = await storageClient.ListStoragesAsync(new ListStoragesRequest(), cancellationToken: ct);
    var dtos = resp.Storages.Select(s => new StorageDto(s.StorageId, s.RootPath, s.ReservedBytes)).ToList();
    return Ok(dtos);
  }

  [HttpPost]
  public async Task<IActionResult> AddStorage([FromBody] StorageDto dto, CancellationToken ct)
  {
    if (string.IsNullOrWhiteSpace(dto.StorageId))
      return BadRequest(new { error = "storageId is required" });
    if (string.IsNullOrWhiteSpace(dto.RootPath))
      return BadRequest(new { error = "rootPath is required" });

    var resp = await storageClient.AddStorageAsync(new Storage
    {
      StorageId     = dto.StorageId,
      RootPath      = dto.RootPath,
      ReservedBytes = dto.ReservedBytes
    }, cancellationToken: ct);

    if (!resp.Success)
      return BadRequest(new { error = resp.Message });

    return Ok(new ApiResponse(true, "ok"));
  }

  [HttpDelete("{storageId}")]
  public async Task<IActionResult> RemoveStorage(string storageId, CancellationToken ct)
  {
    var resp = await storageClient.RemoveStorageAsync(
      new StorageRequest { StorageId = storageId }, cancellationToken: ct);

    if (!resp.Success)
      return NotFound(new { error = resp.Message });

    return Ok(new ApiResponse(true, "ok"));
  }

  [HttpGet("get_camera_storages/{cameraId}")]
  public async Task<IActionResult> GetCameraStorages(string cameraId, CancellationToken ct)
  {
    var resp = await storageClient.GetCameraStoragesAsync(
      new CameraStoragesRequest { CameraId = cameraId }, cancellationToken: ct);

    var dtos = resp.Assignments
      .Select(a => new CameraStorageAssignmentDto(a.CameraId, a.StorageId, a.Priority))
      .ToList();

    return Ok(dtos);
  }

  [HttpPut("set_camera_storages/{cameraId}")]
  public async Task<IActionResult> SetCameraStorages(
    string cameraId,
    [FromBody] List<CameraStorageAssignmentDto> assignments,
    CancellationToken ct)
  {
    var req = new SetCameraStoragesRequest { CameraId = cameraId };
    req.Assignments.AddRange(assignments.Select(a => new CameraStorageAssignment
    {
      CameraId  = cameraId,
      StorageId = a.StorageId,
      Priority  = a.Priority
    }));

    var resp = await storageClient.SetCameraStoragesAsync(req, cancellationToken: ct);

    if (!resp.Success)
      return BadRequest(new { error = resp.Message });

    return Ok(new ApiResponse(true, "ok"));
  }
}

public record StorageDto(string StorageId, string RootPath, ulong ReservedBytes);
public record CameraStorageAssignmentDto(string CameraId, string StorageId, int Priority);
