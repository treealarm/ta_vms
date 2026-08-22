using Grpc.Core;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Vms.Cfg;
using Vms.Fs;
using web_vms.Authentication;
using web_vms.Types;

namespace web_vms.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize(Policy = AuthPolicies.AdminOnly)]
public sealed class StorageController(
  StorageService.StorageServiceClient storageClient,
  ServerPairRegistry registry) : ControllerBase
{
  // Long enough for a slow (network) volume to answer, short enough that an off node fails while
  // the operator is still looking at the dialog.
  private static readonly TimeSpan BrowseTimeout = TimeSpan.FromSeconds(15);

  [HttpGet]
  public async Task<IActionResult> ListStorages(CancellationToken ct)
  {
    var resp = await storageClient.ListStoragesAsync(new ListStoragesRequest(), cancellationToken: ct);
    var dtos = resp.Storages.Select(s => new StorageDto(s.StorageId, s.RootPath, s.ReservedBytes, s.Disabled, s.CameraCount)).ToList();
    return Ok(dtos);
  }

  [HttpPost]
  public async Task<IActionResult> AddStorage([FromBody] StorageDto dto, CancellationToken ct)
  {
    if (string.IsNullOrWhiteSpace(dto.StorageId))
      return BadRequest(new { error = "storageId is required" });
    if (string.IsNullOrWhiteSpace(dto.RootPath))
      return BadRequest(new { error = "rootPath is required" });
    // Either OS style: the path belongs to the recording node, which may well run Windows while
    // this gateway runs on Linux.
    if (!HostPath.IsAbsolute(dto.RootPath))
      return BadRequest(new { error = "rootPath must be an absolute path, e.g. /srv/video or D:\\video" });

    var resp = await storageClient.AddStorageAsync(new Storage
    {
      StorageId     = dto.StorageId,
      RootPath      = dto.RootPath,
      ReservedBytes = dto.ReservedBytes,
      Disabled      = dto.Disabled
    }, cancellationToken: ct);

    if (!resp.Success)
      return BadRequest(new { error = resp.Message });

    return Ok(new ApiResponse(true, "ok"));
  }

  /// <summary>
  /// Directories on <paramref name="serverId"/>'s disk that its file service can write into.
  /// Empty <paramref name="path"/> asks for the volume roots.
  /// </summary>
  /// <remarks>
  /// A pure proxy: the path is not parsed, normalized or converted here. Only the node that owns
  /// the disk can say what exists on it, so this endpoint's whole job is to reach the right one.
  /// </remarks>
  [HttpGet("browse")]
  public async Task<IActionResult> BrowseDirectories(
    [FromQuery] string? serverId, [FromQuery] string? path, CancellationToken ct)
  {
    if (string.IsNullOrWhiteSpace(serverId))
      return BadRequest(new { error = "serverId is required" });

    FsService.FsServiceClient fsClient;
    try
    {
      fsClient = registry.GetFsClient(serverId);
    }
    catch (InvalidOperationException ex)
    {
      // An unknown server or one with no FS paired is a bad request, not a server fault.
      return BadRequest(new { error = ex.Message });
    }

    ListDirectoriesResponse resp;
    try
    {
      resp = await fsClient.ListDirectoriesAsync(
        new ListDirectoriesRequest { Path = path ?? "" },
        deadline: DateTime.UtcNow.Add(BrowseTimeout), cancellationToken: ct);
    }
    catch (RpcException ex)
    {
      // A node that is down is the ordinary case here — the picker lists every configured server,
      // including ones that are off. Answered in place rather than through RpcExceptionFilter so
      // the operator gets "that node did not answer" instead of daprd's address-resolution wording.
      return StatusCode(StatusCodes.Status503ServiceUnavailable, new
      {
        error = $"Recording node '{serverId}' did not answer: {ex.Status.Detail}"
      });
    }

    return Ok(new DirectoryListingDto(
      resp.Path,
      resp.ParentPath,
      resp.Entries
        .Select(e => new DirectoryEntryDto(e.Path, e.Name, e.FreeBytes, e.TotalBytes, e.Writable))
        .ToList(),
      resp.Message,
      resp.Writable));
  }

  [HttpPut("{storageId}")]
  public async Task<IActionResult> UpdateStorage(string storageId, [FromBody] StorageDto dto, CancellationToken ct)
  {
    if (storageId != dto.StorageId)
      return BadRequest(new { error = "storageId mismatch" });
    if (string.IsNullOrWhiteSpace(dto.RootPath))
      return BadRequest(new { error = "rootPath is required" });
    if (!HostPath.IsAbsolute(dto.RootPath))
      return BadRequest(new { error = "rootPath must be an absolute path, e.g. /srv/video or D:\\video" });

    var resp = await storageClient.AddStorageAsync(new Storage
    {
      StorageId     = dto.StorageId,
      RootPath      = dto.RootPath,
      ReservedBytes = dto.ReservedBytes,
      Disabled      = dto.Disabled
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

public record StorageDto(string StorageId, string RootPath, ulong ReservedBytes, bool Disabled = false, uint CameraCount = 0);
/// <param name="Writable">False for a directory that cannot hold the archive — still listed, so a
/// writable directory inside it stays reachable, but it cannot be picked.</param>
public record DirectoryEntryDto(
  string Path, string Name, ulong FreeBytes, ulong TotalBytes, bool Writable);
/// <param name="ParentPath">Empty when the listing is (or came from) the volume roots.</param>
/// <param name="Writable">Whether <paramref name="Path"/> itself can be picked as a storage root.</param>
public record DirectoryListingDto(
  string Path, string ParentPath, List<DirectoryEntryDto> Entries, string Message, bool Writable);
public record CameraStorageAssignmentDto(string CameraId, string StorageId, int Priority);
