using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using web_srv.Authentication;
using web_srv.Authorization;
using MapProto = Vms.Map;

namespace web_srv;

// Plan pictures, diagram backgrounds and event crops are rows of `files` in ta_map, not files on a
// volume: VmsMap is replicable, so a path would tie the row to one instance. This streams the row
// back in the chunks the gRPC boundary carries.
[ApiController]
[Authorize(Policy = AuthPolicies.Authenticated)]
[Route("api/map/files")]
[NoRights("A file is addressed by its own id; the node that references it was filtered on read")]
public class MapFilesController(MapProto.MapService.MapServiceClient map) : ControllerBase
{
  [HttpGet("{id}")]
  public async Task<IActionResult> Get(string id, CancellationToken ct)
  {
    MapProto.FileInfo info;
    try
    {
      info = await map.GetFileInfoAsync(
        new MapProto.FileRequest { Id = id }, MapApi.Options(ct));
    }
    catch (RpcException ex) when (ex.StatusCode == Grpc.Core.StatusCode.NotFound)
    {
      return NotFound();
    }

    Response.ContentType = info.Mime;
    if (!string.IsNullOrEmpty(info.Etag))
      Response.Headers.ETag = $"\"{info.Etag}\"";

    using var call = map.GetFile(
      new MapProto.FileRequest { Id = id },
      new CallOptions(cancellationToken: ct));

    await foreach (var chunk in call.ResponseStream.ReadAllAsync(ct))
      await Response.Body.WriteAsync(chunk.Data.Memory, ct);

    return new EmptyResult();
  }
}
