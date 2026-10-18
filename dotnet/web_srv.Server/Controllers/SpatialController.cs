using Grpc.Core;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Vms.Cfg;
using web_srv.Authentication;
using web_srv.Authorization;
using web_srv.Types.Dto;

namespace web_srv;

/// <summary>Spaces and their landmarks: the worlds cameras are placed in.</summary>
/// <remarks>
/// Reading the spaces is open to every signed-in user, because the operator's map is drawn on them;
/// a space is not a camera and shows no video. Landmarks exist only to calibrate cameras, so they
/// are an administrator's business throughout.
/// </remarks>
[ApiController]
[Route("api/spatial")]
public class SpatialController(CfgService.CfgServiceClient _cfg) : ControllerBase
{
  private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

  private static CallOptions Options(CancellationToken ct) =>
    new(deadline: DateTime.UtcNow.Add(Timeout), cancellationToken: ct);

  [HttpGet("spaces")]
  [Authorize(Policy = AuthPolicies.Authenticated)]
  [NoCameraAccess("Spaces are the map itself, not a camera; placements are filtered on their own endpoint")]
  public async Task<ActionResult<IReadOnlyList<SpaceDto>>> ListSpaces(CancellationToken ct)
  {
    var response = await _cfg.ListSpacesAsync(new Google.Protobuf.WellKnownTypes.Empty(), Options(ct));
    return response.Spaces.Select(SpatialMapper.ToDto).ToList();
  }

  [HttpPut("spaces")]
  [Authorize(Policy = AuthPolicies.AdminOnly)]
  public async Task<ActionResult<SpaceDto>> UpsertSpace([FromBody] SpaceDto dto, CancellationToken ct) =>
    SpatialMapper.ToDto(await _cfg.UpsertSpaceAsync(SpatialMapper.ToProto(dto), Options(ct)));

  [HttpDelete("spaces/{spaceId}")]
  [Authorize(Policy = AuthPolicies.AdminOnly)]
  public async Task<IActionResult> RemoveSpace(string spaceId, CancellationToken ct) =>
    Result(await _cfg.RemoveSpaceAsync(new SpaceRequest { SpaceId = spaceId }, Options(ct)));

  [HttpGet("spaces/{spaceId}/landmarks")]
  [Authorize(Policy = AuthPolicies.AdminOnly)]
  public async Task<ActionResult<IReadOnlyList<LandmarkDto>>> ListLandmarks(string spaceId, CancellationToken ct)
  {
    var response = await _cfg.ListLandmarksAsync(new SpaceRequest { SpaceId = spaceId }, Options(ct));
    return response.Landmarks.Select(SpatialMapper.ToDto).ToList();
  }

  [HttpPut("landmarks")]
  [Authorize(Policy = AuthPolicies.AdminOnly)]
  public async Task<ActionResult<LandmarkDto>> UpsertLandmark([FromBody] LandmarkDto dto, CancellationToken ct) =>
    SpatialMapper.ToDto(await _cfg.UpsertLandmarkAsync(SpatialMapper.ToProto(dto), Options(ct)));

  [HttpDelete("landmarks/{landmarkId}")]
  [Authorize(Policy = AuthPolicies.AdminOnly)]
  public async Task<IActionResult> RemoveLandmark(string landmarkId, CancellationToken ct) =>
    Result(await _cfg.RemoveLandmarkAsync(new LandmarkRequest { LandmarkId = landmarkId }, Options(ct)));

  /// Same body as RpcExceptionFilter produces, so the client reads one error shape.
  internal static IActionResult Result(OperationStatus status) =>
    status.Success ? new OkResult() : new BadRequestObjectResult(new { error = status.Message });
}

/// <summary>Where cameras are drawn, in every space.</summary>
/// <remarks>
/// The list is filtered by the caller's live-view grants, the same way the camera list is: a
/// camera the operator may not watch does not appear on the operator's map either. Placing a camera
/// is configuration and belongs to administrators.
/// </remarks>
[ApiController]
[Route("api/spatial/placements")]
public class CameraPlacementController(
  CfgService.CfgServiceClient _cfg,
  ICameraAccessService _access) : ControllerBase
{
  private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

  private static CallOptions Options(CancellationToken ct) =>
    new(deadline: DateTime.UtcNow.Add(Timeout), cancellationToken: ct);

  [HttpGet]
  [CameraAccess(CameraAction.ViewLive, Source = CameraIdSource.Action)]
  public async Task<ActionResult<IReadOnlyList<CameraPlacementDto>>> List(CancellationToken ct)
  {
    var response = await _cfg.ListCameraPlacementsAsync(new Google.Protobuf.WellKnownTypes.Empty(), Options(ct));
    var scope = await _access.ScopeAsync(User, CameraAction.ViewLive, ct);
    return scope.Filter(response.Placements, p => p.CameraId).Select(SpatialMapper.ToDto).ToList();
  }

  [HttpPut("{cameraId}/{spaceId}")]
  [Authorize(Policy = AuthPolicies.AdminOnly)]
  public async Task<IActionResult> Upsert(string cameraId, string spaceId, [FromBody] CameraPlacementDto dto, CancellationToken ct) =>
    SpatialController.Result(await _cfg.UpsertCameraPlacementAsync(SpatialMapper.ToProto(cameraId, spaceId, dto), Options(ct)));

  [HttpDelete("{cameraId}/{spaceId}")]
  [Authorize(Policy = AuthPolicies.AdminOnly)]
  public async Task<IActionResult> Remove(string cameraId, string spaceId, CancellationToken ct) =>
    SpatialController.Result(await _cfg.RemoveCameraPlacementAsync(
      new CameraPlacementRequest { CameraId = cameraId, SpaceId = spaceId }, Options(ct)));
}
