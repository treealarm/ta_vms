using Grpc.Core;
using Microsoft.AspNetCore.Mvc;
using Vms.Cfg;
using Vms.Onvif;

namespace web_vms;

[ApiController]
[Route("api/[controller]")]
public class OnvifController(
  OnvifService.OnvifServiceClient onvifClient,
  CfgService.CfgServiceClient cfgClient) : ControllerBase
{
  private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

  private static CallOptions Options(CancellationToken ct) =>
    new(deadline: DateTime.UtcNow.Add(Timeout), cancellationToken: ct);

  [HttpGet("cameras/{cameraId}/profiles")]
  public async Task<IActionResult> GetProfiles(string cameraId, CancellationToken ct)
  {
    try
    {
      var response = await onvifClient.GetProfilesAsync(
        new CameraRequest { SourceId = cameraId }, Options(ct));

      return Ok(response.Profiles.Select(p => new
      {
        token    = p.Token,
        name     = p.Name,
        width    = p.Width,
        height   = p.Height,
        encoding = p.Encoding
      }));
    }
    catch (RpcException ex) when (ex.StatusCode == Grpc.Core.StatusCode.DeadlineExceeded)
    {
      return StatusCode(504, "Camera did not respond within timeout");
    }
    catch (RpcException ex)
    {
      return StatusCode(502, ex.Status.Detail);
    }
  }

  [HttpGet("cameras/{cameraId}/stream-uri")]
  public async Task<IActionResult> GetStreamUri(string cameraId, [FromQuery] string? profileToken, CancellationToken ct)
  {
    try
    {
      var response = await onvifClient.GetStreamUriAsync(
        new StreamUriRequest { SourceId = cameraId, ProfileToken = profileToken ?? "" },
        Options(ct));

      return Ok(new { rtspUrl = response.RtspUrl });
    }
    catch (RpcException ex) when (ex.StatusCode == Grpc.Core.StatusCode.DeadlineExceeded)
    {
      return StatusCode(504, "Camera did not respond within timeout");
    }
    catch (RpcException ex)
    {
      return StatusCode(502, ex.Status.Detail);
    }
  }

  // ── ONVIF alarm filters ──────────────────────────────────────────────────

  [HttpGet("cameras/{cameraId}/alarm-filters")]
  public async Task<IActionResult> ListAlarmFilters(string cameraId, CancellationToken ct)
  {
    try
    {
      var resp = await cfgClient.ListOnvifAlarmFiltersAsync(
        new ListOnvifAlarmFiltersRequest { CameraId = cameraId }, Options(ct));
      return Ok(resp.Filters.Select(f => new
      {
        id             = f.Id,
        cameraId       = f.CameraId,
        filterText     = f.FilterText,
        timeoutSeconds = f.TimeoutSeconds,
      }));
    }
    catch (RpcException ex)
    {
      return StatusCode(502, ex.Status.Detail);
    }
  }

  [HttpPost("cameras/{cameraId}/alarm-filters")]
  public async Task<IActionResult> UpsertAlarmFilter(
    string cameraId,
    [FromBody] OnvifAlarmFilterBody body,
    CancellationToken ct)
  {
    try
    {
      var result = await cfgClient.UpsertOnvifAlarmFilterAsync(
        new OnvifAlarmFilter
        {
          Id             = body.Id ?? string.Empty,
          CameraId       = cameraId,
          FilterText     = body.FilterText,
          TimeoutSeconds = body.TimeoutSeconds > 0 ? body.TimeoutSeconds : 30,
        },
        Options(ct));
      return Ok(new
      {
        id             = result.Id,
        cameraId       = result.CameraId,
        filterText     = result.FilterText,
        timeoutSeconds = result.TimeoutSeconds,
      });
    }
    catch (RpcException ex) when (ex.StatusCode == Grpc.Core.StatusCode.InvalidArgument)
    {
      return BadRequest(ex.Status.Detail);
    }
    catch (RpcException ex)
    {
      return StatusCode(502, ex.Status.Detail);
    }
  }

  [HttpDelete("cameras/{cameraId}/alarm-filters/{filterId}")]
  public async Task<IActionResult> RemoveAlarmFilter(
    string cameraId, string filterId, CancellationToken ct)
  {
    try
    {
      var resp = await cfgClient.RemoveOnvifAlarmFilterAsync(
        new OnvifAlarmFilterRequest { Id = filterId }, Options(ct));
      return resp.Success ? Ok() : NotFound();
    }
    catch (RpcException ex)
    {
      return StatusCode(502, ex.Status.Detail);
    }
  }

  // ── Subscriptions (arm / disarm) ─────────────────────────────────────────

  [HttpGet("subscriptions")]
  public async Task<IActionResult> ListSubscriptions(CancellationToken ct)
  {
    try
    {
      var resp = await cfgClient.ListOnvifSubscriptionsAsync(
        new Google.Protobuf.WellKnownTypes.Empty(), Options(ct));
      return Ok(resp.CameraIds);
    }
    catch (RpcException ex) { return StatusCode(502, ex.Status.Detail); }
  }

  [HttpPost("cameras/{cameraId}/subscription")]
  public async Task<IActionResult> Subscribe(string cameraId, CancellationToken ct)
  {
    try
    {
      await cfgClient.SubscribeOnvifEventsAsync(
        new OnvifSubscriptionRequest { CameraId = cameraId }, Options(ct));
      return Ok();
    }
    catch (RpcException ex) { return StatusCode(502, ex.Status.Detail); }
  }

  [HttpDelete("cameras/{cameraId}/subscription")]
  public async Task<IActionResult> Unsubscribe(string cameraId, CancellationToken ct)
  {
    try
    {
      await cfgClient.UnsubscribeOnvifEventsAsync(
        new OnvifSubscriptionRequest { CameraId = cameraId }, Options(ct));
      return Ok();
    }
    catch (RpcException ex) { return StatusCode(502, ex.Status.Detail); }
  }

  // ── ONVIF debug events ────────────────────────────────────────────────────

  [HttpGet("cameras/{cameraId}/recent-events")]
  public async Task<IActionResult> GetRecentEvents(string cameraId, CancellationToken ct)
  {
    try
    {
      var resp = await onvifClient.GetRecentOnvifEventsAsync(
        new GetRecentOnvifEventsRequest { CameraId = cameraId }, Options(ct));
      return Ok(resp.Events.Select(e => new
      {
        receivedAt = e.ReceivedAt,
        topic      = e.Topic,
        payloadXml = e.PayloadXml,
      }));
    }
    catch (RpcException ex)
    {
      return StatusCode(502, ex.Status.Detail);
    }
  }
}

public record OnvifAlarmFilterBody(string? Id, string FilterText, int TimeoutSeconds);
