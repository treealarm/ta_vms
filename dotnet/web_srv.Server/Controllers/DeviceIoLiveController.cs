using System.Text.Json;
using System.Threading.Channels;
using Grpc.Core;
using Microsoft.AspNetCore.Mvc;
using Vms.Onvif;
using web_srv.Authorization;

namespace web_srv;

// Server-Sent Events stream of relay and digital-input states for the whole deployment. The monitor
// opens ONE connection and routes each update by camera id: a connection per tile would spend the
// browser's six HTTP/1.1 slots on state alone and stall live video, which is the same reason the
// analytics overlay keeps one stream.
//
// Every node pushes its own cameras; web_srv merges those gRPC streams into this one response and
// filters each update against the caller's live-view scope, re-read per update so a revocation
// lands on an open stream.
[ApiController]
[Route("api/DeviceIo")]
public class DeviceIoLiveController(ServerPairRegistry registry, IRightsService access) : ControllerBase
{
  private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
  private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(15);

  // The action filters every update itself: there is no one camera in the route to check.
  [RequiresRight(RightActions.ViewLive, Source = RightSource.Action)]
  [HttpGet("live")]
  public async Task Live(CancellationToken ct)
  {
    // Read before the response starts: without VmsCfg's pairing there is no node to ask, and the
    // client should get an error rather than an empty stream that never says anything.
    var onvifIds = registry.AllOnvifIds;

    Response.Headers.ContentType = "text/event-stream";
    Response.Headers.CacheControl = "no-cache";
    Response.Headers["X-Accel-Buffering"] = "no";

    var queue = Channel.CreateUnbounded<DeviceIoStateUpdate>(new UnboundedChannelOptions
    {
      SingleReader = true,
      SingleWriter = false,
    });
    var pumps = onvifIds.Select(id => PumpAsync(id, queue.Writer, ct)).ToArray();
    var allPumps = Task.WhenAll(pumps);

    try
    {
      await WriteRawAsync(": connected\n\n", ct);

      // One pending read at a time: the heartbeat must not start a second reader on a channel whose
      // values a single consumer takes.
      var pending = queue.Reader.ReadAsync(ct).AsTask();
      while (true)
      {
        var finished = await Task.WhenAny(pending, Task.Delay(HeartbeatInterval, ct), allPumps);
        if (finished == allPumps)
        {
          // Every node's stream is gone. End the response so the browser reconnects and opens new
          // ones when the nodes are back, rather than holding an idle connection forever.
          await allPumps;
          break;
        }
        if (finished != pending)
        {
          await WriteRawAsync(":\n\n", ct);
          continue;
        }

        var update = await pending;
        pending = queue.Reader.ReadAsync(ct).AsTask();

        var scope = await access.ScopeAsync(User, RightActions.ViewLive, ct);
        if (!scope.Contains(update.SourceId))
          continue;

        var dto = new DeviceIoUpdateDto(
          update.SourceId,
          update.PartKind,
          update.Token,
          update.LogicalState,
          update.HasContact ? update.Contact : null,
          update.Initialized,
          update.ObservedAt,
          update.Pending);
        await WriteRawAsync($"data: {JsonSerializer.Serialize(dto, JsonOptions)}\n\n", ct);
      }
    }
    catch (OperationCanceledException)
    {
      // The browser navigated away or closed the overlay.
    }
    catch (RpcException)
    {
      // A node restarted; the client reconnects and the initial snapshots put the states back.
    }
    finally
    {
      queue.Writer.TryComplete();
    }
  }

  private async Task PumpAsync(string onvifId, ChannelWriter<DeviceIoStateUpdate> writer, CancellationToken ct)
  {
    try
    {
      using var call = registry.DeviceIoClientFor(onvifId)
        .StreamDeviceIoStates(new StreamDeviceIoStatesRequest(), new CallOptions(cancellationToken: ct));
      await foreach (var update in call.ResponseStream.ReadAllAsync(ct))
        await writer.WriteAsync(update, ct);
    }
    catch (OperationCanceledException)
    {
    }
    catch (RpcException)
    {
      // A node that is down contributes nothing; the others keep their updates flowing.
    }
  }

  private async Task WriteRawAsync(string text, CancellationToken ct)
  {
    await Response.WriteAsync(text, ct);
    await Response.Body.FlushAsync(ct);
  }

  private sealed record DeviceIoUpdateDto(
    string SourceId,
    string PartKind,
    string Token,
    bool LogicalState,
    string? Contact,
    bool Initialized,
    string ObservedAt,
    bool Pending);
}
