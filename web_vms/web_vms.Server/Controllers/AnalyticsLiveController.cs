using System.Text.Json;
using Grpc.Core;
using Microsoft.AspNetCore.Mvc;
using Vms.Analytics;
using web_vms.Types.Dto;

namespace web_vms;

// Server-Sent Events stream of live detections per camera, feeding the bbox overlay drawn over live
// video in the monitor. Consumed by the frontend via fetch-streaming (not EventSource), so the
// existing Bearer wrapper still applies. Anonymous to match the sibling live-view endpoints (WHEP
// video, camera state): the video itself is served without [Authorize], so gating its annotations
// behind auth would be inconsistent and pointless.
[ApiController]
[Route("api/analytics")]
public class AnalyticsLiveController(
  VmsAnalyticsService.VmsAnalyticsServiceClient _analyticsClient) : ControllerBase
{
  private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

  [HttpGet("live/{cameraId}")]
  public async Task Live(string cameraId, CancellationToken ct)
  {
    Response.Headers.ContentType = "text/event-stream";
    Response.Headers.CacheControl = "no-cache";
    Response.Headers["X-Accel-Buffering"] = "no"; // don't let a reverse proxy buffer the stream

    // Open the SSE connection immediately so the client's reader unblocks before the first
    // detection (which may be seconds away, or never for an idle camera).
    await Response.WriteAsync(": connected\n\n", ct);
    await Response.Body.FlushAsync(ct);

    using var call = _analyticsClient.StreamCameraDetections(
      new StreamCameraDetectionsRequest { CameraId = cameraId },
      new CallOptions(cancellationToken: ct));

    try
    {
      await foreach (var e in call.ResponseStream.ReadAllAsync(ct))
      {
        var dto = new AnalyticsActivityEntryDto
        {
          CameraId       = e.CameraId,
          TrackId        = e.TrackId,
          Kind           = AnalyticsKindMapper.ToStringId(e.Kind),
          Confidence     = e.Confidence,
          BboxX          = e.Bbox?.X ?? 0,
          BboxY          = e.Bbox?.Y ?? 0,
          BboxWidth      = e.Bbox?.Width ?? 0,
          BboxHeight     = e.Bbox?.Height ?? 0,
          DetectedAt     = e.DetectedAt.ToDateTime(),
          PlateText      = e.HasPlateText ? e.PlateText : null,
          CropJpegBase64 = null, // geometry + labels only; crops are never streamed
          Persisted      = e.Persisted,
        };
        await Response.WriteAsync($"data: {JsonSerializer.Serialize(dto, JsonOpts)}\n\n", ct);
        await Response.Body.FlushAsync(ct);
      }
    }
    catch (OperationCanceledException)
    {
      // Client disconnected (toggle off / navigated away) — normal SSE teardown.
    }
  }
}
