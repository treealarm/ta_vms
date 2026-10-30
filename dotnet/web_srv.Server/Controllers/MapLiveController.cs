using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using web_srv.Authentication;
using web_srv.Authorization;
using MapProto = Vms.Map;

namespace web_srv;

// Server-Sent Events over MapService.Watch, the same shape Square's map socket had: the client
// sends what it follows (a box or a set of ids), and the server sends the ids that changed. The
// payload is fetched over REST, so a reconnect costs one diff rather than a whole redraw.
[ApiController]
[Authorize(Policy = AuthPolicies.Authenticated)]
[Route("api/map")]
[NoRights("VmsMap filters every watch message by the caller's rights")]
public class MapLiveController(MapProto.MapService.MapServiceClient map) : ControllerBase
{
  private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(15);

  [HttpGet("live")]
  public async Task Live(
    [FromQuery] string? ids, [FromQuery] bool values,
    [FromQuery] double? north, [FromQuery] double? south,
    [FromQuery] double? east, [FromQuery] double? west, [FromQuery] double? zoom,
    CancellationToken ct)
  {
    var hasBox = north.HasValue && south.HasValue && east.HasValue && west.HasValue;
    var idList = (ids ?? string.Empty)
      .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
      .ToList();

    if (!hasBox && idList.Count == 0)
    {
      Response.StatusCode = StatusCodes.Status400BadRequest;
      await Response.WriteAsync("ids or a box is required", ct);
      return;
    }

    Response.Headers.ContentType = "text/event-stream";
    Response.Headers.CacheControl = "no-cache";
    Response.Headers["X-Accel-Buffering"] = "no";

    var command = new MapProto.WatchRequest { Caller = User.ToMapCaller() };
    if (hasBox)
    {
      command.SetBox = new MapProto.WatchBox
      {
        Box = new MapProto.Box
        {
          Wn = new MapProto.Coordinate { Lat = north!.Value, Lon = west!.Value },
          Es = new MapProto.Coordinate { Lat = south!.Value, Lon = east!.Value },
          Zoom = zoom ?? 0,
        },
      };
      if (!zoom.HasValue)
        command.SetBox.Box.ClearZoom();
    }
    else
    {
      command.SetIds = new MapProto.WatchIds { Ids = { idList } };
    }

    if (values)
      command.UpdateValues = new MapProto.WatchValues { Enabled = true };

    try
    {
      using var call = map.Watch(command, new CallOptions(cancellationToken: ct));
      await WriteRawAsync(": connected\n\n", ct);

      var stream = call.ResponseStream;
      var moveNext = stream.MoveNext(ct);
      while (true)
      {
        var finished = await Task.WhenAny(moveNext, Task.Delay(HeartbeatInterval, ct));
        if (finished != moveNext)
        {
          await WriteRawAsync(":\n\n", ct);
          continue;
        }

        if (!await moveNext)
          break;

        // VmsMap already applied the rights rule to this message; the web re-checks on the REST
        // read the client makes next, so a revocation lands without tearing the stream down.
        var json = MapApi.Formatter.Format(stream.Current);
        await WriteRawAsync($"data: {json}\n\n", ct);
        moveNext = stream.MoveNext(ct);
      }
    }
    catch (OperationCanceledException)
    {
      // Client disconnected.
    }
    catch (RpcException)
    {
      // The map service restarted or cancelled the stream; the client reconnects.
    }
  }

  private async Task WriteRawAsync(string text, CancellationToken ct)
  {
    await Response.WriteAsync(text, ct);
    await Response.Body.FlushAsync(ct);
  }
}
