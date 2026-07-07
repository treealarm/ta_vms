using System.Text.Json;
using Grpc.Core;
using Microsoft.AspNetCore.Mvc;
using Vms.Analytics;
using web_vms.Types.Dto;

namespace web_vms;

// Server-Sent Events stream of live detections for ALL cameras, feeding the bbox overlay drawn over
// live video in the monitor. The frontend opens ONE connection for the whole monitor and routes
// events to the right camera overlay by cameraId (a per-camera connection would exhaust the browser
// HTTP/1.1 connection pool on large grids). Consumed via fetch-streaming (not EventSource), so the
// existing Bearer wrapper still applies. Anonymous to match the sibling live-view endpoints (WHEP
// video, camera state): the video itself is served without [Authorize], so gating its annotations
// behind auth would be inconsistent and pointless.
[ApiController]
[Route("api/analytics")]
public class AnalyticsLiveController(
  VmsAnalyticsService.VmsAnalyticsServiceClient _analyticsClient) : ControllerBase
{
  private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);
  // Keep-alive comment cadence: without traffic, an idle intermediary could close the connection.
  private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(15);

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

        var dto = AnalyticsActivityMapper.ToDto(stream.Current, includeCrop: false);
        await WriteRawAsync($"data: {JsonSerializer.Serialize(dto, JsonOpts)}\n\n", ct);
        moveNext = stream.MoveNext(ct);
      }
    }
    catch (OperationCanceledException)
    {
      // Client disconnected (navigated away / all overlays off) — normal SSE teardown.
    }
    catch (RpcException)
    {
      // gRPC stream cancelled or VmsAnalytics unavailable/restarting — the response has already
      // started, so there's nothing to return; just end the stream and let the client reconnect.
    }
  }

  private async Task WriteRawAsync(string s, CancellationToken ct)
  {
    await Response.WriteAsync(s, ct);
    await Response.Body.FlushAsync(ct);
  }
}
