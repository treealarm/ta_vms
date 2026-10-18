using Google.Protobuf;
using Grpc.Core;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Vms.Cfg;
using web_srv.Authentication;
using web_srv.Authorization;
using web_srv.Types.Dto;

namespace web_srv;

/// <summary>A plan space's picture: uploading it, aligning it in its space, and serving it.</summary>
/// <remarks>
/// The image lives in VmsCfg's database, not on this service's disk: web_srv runs as several
/// replicas and keeps no files. It travels in pieces both ways, because Dapr service invocation
/// caps one gRPC message at 4 MiB and a plan is often larger.
/// </remarks>
[ApiController]
[Route("api/spatial/spaces/{spaceId}/plan")]
public class SpacePlanController(CfgService.CfgServiceClient _cfg) : ControllerBase
{
  /// Mirrors VmsCfg's limit, so an oversized upload is refused here before it is streamed anywhere.
  private const int MaxImageBytes = 20 * 1024 * 1024;
  private const int ChunkBytes = 256 * 1024;

  // An image of tens of megabytes through two sidecars is not a 5-second call.
  private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

  private static CallOptions Options(CancellationToken ct) =>
    new(deadline: DateTime.UtcNow.Add(Timeout), cancellationToken: ct);

  public sealed class UploadForm
  {
    public IFormFile? File { get; init; }
    public int WidthPx { get; init; }
    public int HeightPx { get; init; }
    public double TlLat { get; init; }
    public double TlLon { get; init; }
    public double TrLat { get; init; }
    public double TrLon { get; init; }
    public double BlLat { get; init; }
    public double BlLon { get; init; }
  }

  [HttpGet]
  [Authorize(Policy = AuthPolicies.Authenticated)]
  [NoCameraAccess("A plan image is part of the map, not a camera")]
  public async Task Get(string spaceId, CancellationToken ct)
  {
    // Parsed rather than trimmed: a proxy that compresses the response weakens the tag to W/"…",
    // and a cache holding several versions lists them all. Any of them still current is a 304.
    var request = new GetSpacePlanImageRequest { SpaceId = spaceId };
    request.IfNoneMatch.AddRange(Request.GetTypedHeaders().IfNoneMatch.Select(t => t.Tag.Value!.Trim('"')));
    using var call = _cfg.GetSpacePlanImage(request, Options(ct));

    if (!await call.ResponseStream.MoveNext(ct) || call.ResponseStream.Current.PartCase != SpacePlanImageChunk.PartOneofCase.Header)
      throw new RpcException(new Status(Grpc.Core.StatusCode.Internal, "VmsCfg sent a plan image without its header"));

    var header = call.ResponseStream.Current.Header;
    // The URL carries the ETag, so a copy is never stale under its own address; the ETag still lets
    // a reload revalidate in one round trip instead of fetching megabytes again.
    Response.Headers.ETag = $"\"{header.Etag}\"";
    Response.Headers.CacheControl = "private, max-age=86400";
    if (header.NotModified)
    {
      Response.StatusCode = StatusCodes.Status304NotModified;
      return;
    }

    Response.ContentType = header.Mime;
    while (await call.ResponseStream.MoveNext(ct))
    {
      var chunk = call.ResponseStream.Current;
      if (chunk.PartCase == SpacePlanImageChunk.PartOneofCase.Data)
        await Response.Body.WriteAsync(chunk.Data.Memory, ct);
    }
  }

  [HttpPost]
  [Authorize(Policy = AuthPolicies.AdminOnly)]
  [RequestSizeLimit(MaxImageBytes + 1024 * 1024)]
  [RequestFormLimits(MultipartBodyLengthLimit = MaxImageBytes + 1024 * 1024)]
  public async Task<ActionResult<SpaceDto>> Upload(string spaceId, [FromForm] UploadForm form, CancellationToken ct)
  {
    if (form.File is null || form.File.Length == 0)
      return BadRequest(new { error = "No image was sent" });
    if (form.File.Length > MaxImageBytes)
      return StatusCode(StatusCodes.Status413PayloadTooLarge,
        new { error = $"A plan image may not exceed {MaxImageBytes / (1024 * 1024)} MiB" });
    if (form.WidthPx <= 0 || form.HeightPx <= 0)
      return BadRequest(new { error = "The image size must be given" });

    using var call = _cfg.UploadSpacePlan(Options(ct));
    await call.RequestStream.WriteAsync(new SpacePlanUploadChunk
    {
      Header = new SpacePlanUploadHeader
      {
        SpaceId = spaceId,
        WidthPx = (uint)form.WidthPx,
        HeightPx = (uint)form.HeightPx,
        Mime = form.File.ContentType,
        Corners = new PlanCorners
        {
          TopLeft = new GeoPoint { Lat = form.TlLat, Lon = form.TlLon },
          TopRight = new GeoPoint { Lat = form.TrLat, Lon = form.TrLon },
          BottomLeft = new GeoPoint { Lat = form.BlLat, Lon = form.BlLon },
        },
      },
    }, ct);

    await using (var input = form.File.OpenReadStream())
    {
      var buffer = new byte[ChunkBytes];
      int read;
      while ((read = await input.ReadAsync(buffer, ct)) > 0)
      {
        await call.RequestStream.WriteAsync(
          new SpacePlanUploadChunk { Data = ByteString.CopyFrom(buffer, 0, read) }, ct);
      }
    }

    await call.RequestStream.CompleteAsync();
    return SpatialMapper.ToDto(await call.ResponseAsync);
  }

  /// Re-aligns the plan in its space without sending the image again.
  [HttpPut("corners")]
  [Authorize(Policy = AuthPolicies.AdminOnly)]
  public async Task<ActionResult<SpaceDto>> SetCorners(string spaceId, [FromBody] PlanCornersDto dto, CancellationToken ct) =>
    SpatialMapper.ToDto(await _cfg.SetSpacePlanCornersAsync(
      new SetSpacePlanCornersRequest { SpaceId = spaceId, Corners = SpatialMapper.ToProto(dto) }, Options(ct)));

  [HttpDelete]
  [Authorize(Policy = AuthPolicies.AdminOnly)]
  public async Task<IActionResult> Remove(string spaceId, CancellationToken ct) =>
    SpatialController.Result(await _cfg.RemoveSpacePlanAsync(new SpaceRequest { SpaceId = spaceId }, Options(ct)));
}
