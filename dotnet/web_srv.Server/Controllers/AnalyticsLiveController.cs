using System.Text.Json;
using Grpc.Core;
using Microsoft.AspNetCore.Mvc;
using Vms.Analytics;
using web_srv.Authorization;
using web_srv.Types.Dto;

namespace web_srv;

// Server-Sent Events stream of live detections for ALL cameras, feeding the bbox overlay drawn over
// live video in the monitor. The frontend opens ONE connection for the whole monitor and routes
// events to the right camera overlay by cameraId (a per-camera connection would exhaust the browser
// HTTP/1.1 connection pool on large grids). Consumed via fetch-streaming (not EventSource), so the
// existing Bearer wrapper still applies — like every non-anonymous endpoint here, it needs a valid
// token, and each event is additionally filtered against the caller's camera permissions.
[ApiController]
[Route("api/analytics")]
public class AnalyticsLiveController(
  VmsAnalyticsService.VmsAnalyticsServiceClient _analyticsClient,
  ICameraAccessService _access) : ControllerBase
{
  private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);
  // Keep-alive comment cadence: without traffic, an idle intermediary could close the connection.
  private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(15);

  [CameraAccess(CameraAction.ViewLive, Source = CameraIdSource.Action)]
  [HttpGet("live")]
  public async Task Live(CancellationToken ct)
  {
    Response.Headers.ContentType = "text/event-stream";
    Response.Headers.CacheControl = "no-cache";
    Response.Headers["X-Accel-Buffering"] = "no"; // don't let a reverse proxy buffer the stream

    // Empty camera_id => all cameras.
    using var call = _analyticsClient.StreamCameraDetections(
      new StreamCameraDetectionsRequest { CameraId = "" },
      new CallOptions(cancellationToken: ct));

    try
    {
      // Open the SSE connection immediately so the client's reader unblocks before the first
      // detection (which may be seconds away, or never for an idle monitor).
      await WriteRawAsync(": connected\n\n", ct);

      var stream = call.ResponseStream;
      var moveNext = stream.MoveNext(ct);
      while (true)
      {
        // Race the next detection against the heartbeat interval so idle connections still get a
        // periodic ping and aren't dropped by a proxy.
        var finished = await Task.WhenAny(moveNext, Task.Delay(HeartbeatInterval, ct));
        if (finished != moveNext)
        {
          await WriteRawAsync(":\n\n", ct); // SSE comment heartbeat
          continue; // keep awaiting the same moveNext
        }

        if (!await moveNext)
          break; // stream ended cleanly

        // Re-read the scope per event rather than once per connection: it is a cache lookup, and it
        // means a revocation takes effect on an already-open stream without tearing it down.
        var scope = await _access.ScopeAsync(User, CameraAction.ViewLive, ct);
        if (scope.Contains(stream.Current.CameraId))
        {
          var dto = AnalyticsActivityMapper.ToDto(stream.Current, includeCrop: false);
          await WriteRawAsync($"data: {JsonSerializer.Serialize(dto, JsonOpts)}\n\n", ct);
        }
        moveNext = stream.MoveNext(ct);
      }
    }
    catch (OperationCanceledException)
    {
      // Client disconnected (navigated away / all overlays off) — normal SSE teardown.
    }
    catch (RpcException)
    {
      // gRPC stream cancelled or VmsAnalyticsSingleton unavailable/restarting — the response has already
      // started, so there's nothing to return; just end the stream and let the client reconnect.
    }
  }

  // The same stream shape for ONVIF events, which cameras with no object analytics produce instead
  // of boxes (cell motion, tamper, line/field crossings). A second endpoint rather than a
  // discriminated union on /live: the two have different payloads and different lifetimes on
  // screen, and a monitor showing only boxes should not pay for event traffic it discards.
  [CameraAccess(CameraAction.ViewLive, Source = CameraIdSource.Action)]
  [HttpGet("live-events")]
  public async Task LiveEvents(CancellationToken ct)
  {
    Response.Headers.ContentType = "text/event-stream";
    Response.Headers.CacheControl = "no-cache";
    Response.Headers["X-Accel-Buffering"] = "no";

    using var call = _analyticsClient.StreamCameraEvents(
      new StreamCameraEventsRequest { CameraId = "" },
      new CallOptions(cancellationToken: ct));

    try
    {
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

        var scope = await _access.ScopeAsync(User, CameraAction.ViewLive, ct);
        if (scope.Contains(stream.Current.CameraId))
        {
          var e = stream.Current;
          var dto = new LiveCameraEventDto
          {
            CameraId   = e.CameraId,
            Topic      = e.Topic,
            Label      = e.Label,
            Active     = e.Active,
            Matched    = e.Matched,
            ReceivedAt = e.ReceivedAt.ToDateTime(),
          };
          await WriteRawAsync($"data: {JsonSerializer.Serialize(dto, JsonOpts)}\n\n", ct);
        }
        moveNext = stream.MoveNext(ct);
      }
    }
    catch (OperationCanceledException)
    {
    }
    catch (RpcException)
    {
    }
  }

  private async Task WriteRawAsync(string s, CancellationToken ct)
  {
    await Response.WriteAsync(s, ct);
    await Response.Body.FlushAsync(ct);
  }
}
