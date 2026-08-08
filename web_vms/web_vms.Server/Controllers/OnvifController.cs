using Dapr.Client;
using Grpc.Core;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Vms.Cfg;
using Vms.Onvif;
using web_vms.Authentication;

namespace web_vms;

[ApiController]
[Route("api/[controller]")]
[Authorize(Policy = AuthPolicies.AdminOnly)]
public class OnvifController(
  OnvifRouter router,
  ServerPairRegistry registry,
  CfgService.CfgServiceClient cfgClient,
  ILogger<OnvifController> logger) : ControllerBase
{
  private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);
  private const int MaxDiscoverTimeoutSeconds = 30;

  // Connecting to a device directly (no cached connection) can chain several sequential
  // WCF calls — device time, GetServices, an auth-scheme retry, then trying MediaService2
  // then MediaService1 — each with its own internal ~15s timeout. A slow/unreachable
  // device can plausibly exceed the generic 15s deadline well before any of those give up.
  private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(60);

  private static CallOptions Options(CancellationToken ct) =>
    new(deadline: DateTime.UtcNow.Add(Timeout), cancellationToken: ct);

  private static CallOptions ConnectOptions(CancellationToken ct) =>
    new(deadline: DateTime.UtcNow.Add(ConnectTimeout), cancellationToken: ct);

  // The probe itself can run as long as requested (clamped); give the call a deadline
  // with margin on top so a long scan doesn't get cut off by the generic 15s deadline.
  private static (int ProbeSeconds, CallOptions Options) DiscoverOptions(int timeoutSeconds, CancellationToken ct)
  {
    var probeSeconds = Math.Clamp(timeoutSeconds <= 0 ? 4 : timeoutSeconds, 1, MaxDiscoverTimeoutSeconds);
    var options = new CallOptions(deadline: DateTime.UtcNow.AddSeconds(probeSeconds + 5), cancellationToken: ct);
    return (probeSeconds, options);
  }

  [HttpGet("cameras/{cameraId}/profiles")]
  public async Task<IActionResult> GetProfiles(string cameraId, CancellationToken ct)
  {
    var response = await (await router.OnvifAsync(cameraId, ct)).GetProfilesAsync(
      new CameraRequest { SourceId = cameraId }, Options(ct));

    return Ok(response.Profiles.Select(ToProfileDto));
  }

  private static object ToProfileDto(OnvifProfile p) => new
  {
    token    = p.Token,
    name     = p.Name,
    width    = p.Width,
    height   = p.Height,
    encoding = p.Encoding
  };

  [HttpGet("cameras/{cameraId}/stream-uri")]
  public async Task<IActionResult> GetStreamUri(string cameraId, [FromQuery] string? profileToken, CancellationToken ct)
  {
    var response = await (await router.OnvifAsync(cameraId, ct)).GetStreamUriAsync(
      new StreamUriRequest { SourceId = cameraId, ProfileToken = profileToken ?? "" },
      Options(ct));

    return Ok(new { rtspUrl = response.RtspUrl });
  }

  // ── Video encoder configuration ──────────────────────────────────────────

  [HttpGet("cameras/{cameraId}/video-encoder-configs")]
  public async Task<IActionResult> GetVideoEncoderConfigs(string cameraId, CancellationToken ct)
  {
    var response = await (await router.OnvifAsync(cameraId, ct)).GetVideoEncoderConfigsAsync(
      new CameraRequest { SourceId = cameraId }, Options(ct));

    return Ok(response.Configs.Select(c => new
    {
      token          = c.Token,
      name           = c.Name,
      encoding       = c.Encoding,
      width          = c.Width,
      height         = c.Height,
      frameRateLimit = c.FrameRateLimit,
      bitrateLimit   = c.BitrateLimit,
      govLength      = c.GovLength,
      h264Profile    = c.H264Profile,
      quality        = c.Quality,
    }));
  }

  [HttpGet("cameras/{cameraId}/video-encoder-config-options")]
  public async Task<IActionResult> GetVideoEncoderConfigOptions(
    string cameraId, [FromQuery] string? configToken, CancellationToken ct)
  {
    var response = await (await router.OnvifAsync(cameraId, ct)).GetVideoEncoderConfigOptionsAsync(
      new VideoEncoderConfigOptionsRequest
      {
        SourceId    = cameraId,
        ConfigToken = configToken ?? string.Empty,
      }, Options(ct));

    return Ok(response.Options.Select(o => new
    {
      encoding     = o.Encoding,
      resolutions  = o.Resolutions.Select(r => new { width = r.Width, height = r.Height }),
      minFrameRate = o.MinFrameRate,
      maxFrameRate = o.MaxFrameRate,
      minBitrate   = o.MinBitrate,
      maxBitrate   = o.MaxBitrate,
      minGovLength = o.MinGovLength,
      maxGovLength = o.MaxGovLength,
      h264Profiles = o.H264Profiles.ToList(),
    }));
  }

  [HttpPut("cameras/{cameraId}/video-encoder-configs/{configToken}")]
  public async Task<IActionResult> SetVideoEncoderConfig(
    string cameraId, string configToken,
    [FromBody] SetVideoEncoderConfigBody body,
    CancellationToken ct)
  {
    var response = await (await router.OnvifAsync(cameraId, ct)).SetVideoEncoderConfigAsync(
      new SetVideoEncoderConfigRequest
      {
        SourceId = cameraId,
        Config   = new VideoEncoderConfig
        {
          Token          = configToken,
          Name           = body.Name ?? string.Empty,
          Encoding       = body.Encoding,
          Width          = body.Width,
          Height         = body.Height,
          FrameRateLimit = body.FrameRateLimit,
          BitrateLimit   = body.BitrateLimit,
          GovLength      = body.GovLength,
          H264Profile    = body.H264Profile ?? string.Empty,
          Quality        = body.Quality,
        },
      }, Options(ct));

    return response.Success ? Ok() : StatusCode(502, response.Error);
  }

  public record SetVideoEncoderConfigBody(
    string? Name,
    string  Encoding,
    int     Width,
    int     Height,
    int     FrameRateLimit,
    int     BitrateLimit,
    int     GovLength,
    string? H264Profile,
    float   Quality);

  // ── Audio encoder configuration ──────────────────────────────────────────

  [HttpGet("cameras/{cameraId}/audio-encoder-configs")]
  public async Task<IActionResult> GetAudioEncoderConfigs(string cameraId, CancellationToken ct)
  {
    var response = await (await router.OnvifAsync(cameraId, ct)).GetAudioEncoderConfigsAsync(
      new CameraRequest { SourceId = cameraId }, Options(ct));
    return Ok(response.Configs.Select(c => new
    {
      token      = c.Token,
      name       = c.Name,
      encoding   = c.Encoding,
      bitrate    = c.Bitrate,
      sampleRate = c.SampleRate,
    }));
  }

  [HttpGet("cameras/{cameraId}/audio-encoder-config-options")]
  public async Task<IActionResult> GetAudioEncoderConfigOptions(
    string cameraId, [FromQuery] string? configToken, CancellationToken ct)
  {
    var response = await (await router.OnvifAsync(cameraId, ct)).GetAudioEncoderConfigOptionsAsync(
      new AudioEncoderConfigOptionsRequest
      {
        SourceId    = cameraId,
        ConfigToken = configToken ?? string.Empty,
      }, Options(ct));
    return Ok(response.Options.Select(o => new
    {
      encoding    = o.Encoding,
      bitrates    = o.Bitrates.ToList(),
      sampleRates = o.SampleRates.ToList(),
    }));
  }

  [HttpPut("cameras/{cameraId}/audio-encoder-configs/{configToken}")]
  public async Task<IActionResult> SetAudioEncoderConfig(
    string cameraId, string configToken,
    [FromBody] SetAudioEncoderConfigBody body,
    CancellationToken ct)
  {
    var response = await (await router.OnvifAsync(cameraId, ct)).SetAudioEncoderConfigAsync(
      new SetAudioEncoderConfigRequest
      {
        SourceId = cameraId,
        Config   = new AudioEncoderConfig
        {
          Token      = configToken,
          Name       = body.Name ?? string.Empty,
          Encoding   = body.Encoding,
          Bitrate    = body.Bitrate,
          SampleRate = body.SampleRate,
        },
      }, Options(ct));
    return response.Success ? Ok() : StatusCode(502, response.Error);
  }

  public record SetAudioEncoderConfigBody(string? Name, string Encoding, int Bitrate, int SampleRate);
  public record SetDeviceTimeBody(string UtcTime);

  // ── Imaging ──────────────────────────────────────────────────────────────

  [HttpGet("cameras/{cameraId}/imaging-settings")]
  public async Task<IActionResult> GetImagingSettings(
    string cameraId, [FromQuery] string? videoSourceToken, CancellationToken ct)
  {
    var response = await (await router.OnvifAsync(cameraId, ct)).GetImagingSettingsAsync(
      new ImagingSettingsRequest { SourceId = cameraId, VideoSourceToken = videoSourceToken ?? string.Empty },
      Options(ct));

    if (!string.IsNullOrEmpty(response.Error))
      return StatusCode(502, response.Error);

    return Ok(new
    {
      brightness      = response.HasBrightness ? response.Brightness : (float?)null,
      contrast        = response.HasContrast ? response.Contrast : (float?)null,
      colorSaturation = response.HasColorSaturation ? response.ColorSaturation : (float?)null,
      sharpness       = response.HasSharpness ? response.Sharpness : (float?)null,
    });
  }

  [HttpGet("cameras/{cameraId}/imaging-options")]
  public async Task<IActionResult> GetImagingOptions(
    string cameraId, [FromQuery] string? videoSourceToken, CancellationToken ct)
  {
    var response = await (await router.OnvifAsync(cameraId, ct)).GetImagingOptionsAsync(
      new ImagingSettingsRequest { SourceId = cameraId, VideoSourceToken = videoSourceToken ?? string.Empty },
      Options(ct));

    if (!string.IsNullOrEmpty(response.Error))
      return StatusCode(502, response.Error);

    static object? ToRange(FloatRange? r) => r == null ? null : new { min = r.Min, max = r.Max };

    return Ok(new
    {
      brightness      = ToRange(response.Brightness),
      contrast        = ToRange(response.Contrast),
      colorSaturation = ToRange(response.ColorSaturation),
      sharpness       = ToRange(response.Sharpness),
    });
  }

  [HttpPut("cameras/{cameraId}/imaging-settings")]
  public async Task<IActionResult> SetImagingSettings(
    string cameraId, [FromBody] SetImagingSettingsBody body, CancellationToken ct)
  {
    var request = new SetImagingSettingsRequest
    {
      SourceId         = cameraId,
      VideoSourceToken = body.VideoSourceToken ?? string.Empty,
    };
    if (body.Brightness.HasValue) request.Brightness = body.Brightness.Value;
    if (body.Contrast.HasValue) request.Contrast = body.Contrast.Value;
    if (body.ColorSaturation.HasValue) request.ColorSaturation = body.ColorSaturation.Value;
    if (body.Sharpness.HasValue) request.Sharpness = body.Sharpness.Value;

    var response = await (await router.OnvifAsync(cameraId, ct)).SetImagingSettingsAsync(request, Options(ct));
    return response.Success ? Ok() : StatusCode(502, response.Error);
  }

  public record SetImagingSettingsBody(
    string? VideoSourceToken, float? Brightness, float? Contrast, float? ColorSaturation, float? Sharpness);

  // ── System ───────────────────────────────────────────────────────────────

  [HttpGet("cameras/{cameraId}/device-time")]
  public async Task<IActionResult> GetDeviceTime(string cameraId, CancellationToken ct)
  {
    var response = await (await router.OnvifAsync(cameraId, ct)).GetDeviceTimeAsync(
      new CameraRequest { SourceId = cameraId }, Options(ct));
    return Ok(new { utcTime = response.UtcTime });
  }

  [HttpPost("cameras/{cameraId}/sync-time")]
  public async Task<IActionResult> SyncDeviceTime(string cameraId, CancellationToken ct)
  {
    var response = await (await router.OnvifAsync(cameraId, ct)).SyncDeviceTimeAsync(
      new CameraRequest { SourceId = cameraId }, Options(ct));
    return response.Success ? Ok() : StatusCode(502, response.Error);
  }

  [HttpPost("cameras/{cameraId}/set-time")]
  public async Task<IActionResult> SetDeviceTime(string cameraId, [FromBody] SetDeviceTimeBody body, CancellationToken ct)
  {
    var response = await (await router.OnvifAsync(cameraId, ct)).SetDeviceTimeAsync(
      new SetDeviceTimeRequest { SourceId = cameraId, UtcTime = body.UtcTime }, Options(ct));
    return response.Success ? Ok() : StatusCode(502, response.Error);
  }

  [HttpGet("cameras/{cameraId}/device-info")]
  public async Task<IActionResult> GetDeviceInformation(string cameraId, CancellationToken ct)
  {
    var response = await (await router.OnvifAsync(cameraId, ct)).GetDeviceInformationAsync(
      new CameraRequest { SourceId = cameraId }, Options(ct));

    if (!string.IsNullOrEmpty(response.Error))
      return StatusCode(502, response.Error);

    return Ok(new
    {
      manufacturer    = response.Manufacturer,
      model           = response.Model,
      firmwareVersion = response.FirmwareVersion,
      serialNumber    = response.SerialNumber,
      hardwareId      = response.HardwareId,
    });
  }

  [HttpGet("cameras/{cameraId}/capabilities")]
  public async Task<IActionResult> GetDeviceCapabilities(string cameraId, CancellationToken ct)
  {
    var response = await (await router.OnvifAsync(cameraId, ct)).GetDeviceCapabilitiesAsync(
      new CameraRequest { SourceId = cameraId }, Options(ct));

    return Ok(new
    {
      hasPtz           = response.HasPtz,
      hasImaging       = response.HasImaging,
      hasEvents        = response.HasEvents,
      hasAudio         = response.HasAudio,
      hasRelayOutputs  = response.HasRelayOutputs,
      hasDigitalInputs = response.HasDigitalInputs,
    });
  }

  [HttpPost("cameras/{cameraId}/reboot")]
  public async Task<IActionResult> RebootDevice(string cameraId, CancellationToken ct)
  {
    var response = await (await router.OnvifAsync(cameraId, ct)).RebootDeviceAsync(
      new CameraRequest { SourceId = cameraId }, Options(ct));
    return response.Success ? Ok(new { message = response.Message }) : StatusCode(502, response.Message);
  }

  // ── Discovery ────────────────────────────────────────────────────────────

  [HttpPost("discover")]
  // serverId is required, not defaulted: discovery is a multicast probe of one node's camera
  // subnet, so "which node" decides which cameras can be found at all. Picking one silently would
  // return an empty list for every other site and look like a network fault.
  public async Task<IActionResult> DiscoverDevices(
    [FromQuery] int timeoutSeconds, [FromQuery] string serverId, CancellationToken ct)
  {
    var (probeSeconds, options) = DiscoverOptions(timeoutSeconds, ct);
    var response = await registry.GetOnvifClient(serverId).DiscoverDevicesAsync(
      new DiscoverDevicesRequest { TimeoutSeconds = probeSeconds }, options);

    return Ok(new
    {
      scanOk = response.ScanOk,
      devices = response.Devices.Select(d => new
      {
        ip           = d.Ip,
        port         = d.Port,
        endpointUuid = d.EndpointUuid,
        xaddrs       = d.Xaddrs.ToList(),
        hardware     = d.Hardware,
        name         = d.Name,
      }),
    });
  }

  [HttpPost("discover/profiles")]
  // Same reason as discovery: the address is only routable from its own node.
  public async Task<IActionResult> GetProfilesByConnection(
    [FromBody] GetProfilesByConnectionBody body, [FromQuery] string serverId, CancellationToken ct)
  {
    var response = await registry.GetOnvifClient(serverId).GetProfilesByConnectionAsync(
      new GetProfilesByConnectionRequest
      {
        Ip    = body.Ip,
        Port  = body.Port,
        User  = body.User ?? string.Empty,
        Pass  = body.Pass ?? string.Empty,
        Xaddr = body.Xaddr ?? string.Empty,
      }, ConnectOptions(ct));

    return Ok(response.Profiles.Select(ToProfileDto));
  }

  public record GetProfilesByConnectionBody(string Ip, int Port, string? User, string? Pass, string? Xaddr);

  // ── ONVIF alarm filters ──────────────────────────────────────────────────

  [HttpGet("cameras/{cameraId}/alarm-filters")]
  public async Task<IActionResult> ListAlarmFilters(string cameraId, CancellationToken ct)
  {
    var resp = await cfgClient.ListOnvifAlarmFiltersAsync(
      new ListOnvifAlarmFiltersRequest { CameraId = cameraId }, Options(ct));
    return Ok(resp.Filters.Select(f => new
    {
      id             = f.Id,
      cameraId       = f.CameraId,
      filterText     = f.FilterText,
      timeoutSeconds = f.TimeoutSeconds,
      textFilter     = f.TextFilter,
    }));
  }

  [HttpPost("cameras/{cameraId}/alarm-filters")]
  public async Task<IActionResult> UpsertAlarmFilter(
    string cameraId,
    [FromBody] OnvifAlarmFilterBody body,
    CancellationToken ct)
  {
    var result = await cfgClient.UpsertOnvifAlarmFilterAsync(
      new OnvifAlarmFilter
      {
        Id             = body.Id ?? string.Empty,
        CameraId       = cameraId,
        FilterText     = body.FilterText,
        TimeoutSeconds = body.TimeoutSeconds > 0 ? body.TimeoutSeconds : 30,
        TextFilter     = body.TextFilter ?? string.Empty,
      },
      Options(ct));
    return Ok(new
    {
      id             = result.Id,
      cameraId       = result.CameraId,
      filterText     = result.FilterText,
      timeoutSeconds = result.TimeoutSeconds,
      textFilter     = result.TextFilter,
    });
  }

  [HttpDelete("cameras/{cameraId}/alarm-filters/{filterId}")]
  public async Task<IActionResult> RemoveAlarmFilter(
    string cameraId, string filterId, CancellationToken ct)
  {
    var filters = await cfgClient.ListOnvifAlarmFiltersAsync(
      new ListOnvifAlarmFiltersRequest { CameraId = cameraId }, Options(ct));
    if (filters.Filters.All(f => f.Id != filterId))
      return NotFound();

    var resp = await cfgClient.RemoveOnvifAlarmFilterAsync(
      new OnvifAlarmFilterRequest { Id = filterId }, Options(ct));
    return resp.Success ? Ok() : NotFound();
  }

  // ── Subscriptions (arm / disarm) ─────────────────────────────────────────

  [HttpGet("subscriptions")]
  public async Task<IActionResult> ListSubscriptions(CancellationToken ct)
  {
    var resp = await cfgClient.ListOnvifSubscriptionsAsync(
      new Google.Protobuf.WellKnownTypes.Empty(), Options(ct));
    return Ok(resp.Cameras.Select(c => new
    {
      cameraId = c.CameraId,
      subscribed = c.Subscribed,
      pollTimeoutSeconds = c.PollTimeoutSeconds,
      messageLimit = c.MessageLimit,
    }));
  }

  [HttpPost("cameras/{cameraId}/subscription")]
  public async Task<IActionResult> Subscribe(string cameraId, CancellationToken ct)
  {
    await cfgClient.SubscribeOnvifEventsAsync(
      new OnvifSubscriptionRequest { CameraId = cameraId }, Options(ct));
    return Ok();
  }

  [HttpDelete("cameras/{cameraId}/subscription")]
  public async Task<IActionResult> Unsubscribe(string cameraId, CancellationToken ct)
  {
    await cfgClient.UnsubscribeOnvifEventsAsync(
      new OnvifSubscriptionRequest { CameraId = cameraId }, Options(ct));
    return Ok();
  }

  [HttpPut("cameras/{cameraId}/subscription")]
  public async Task<IActionResult> SetSubscribed(string cameraId, [FromBody] SetSubscribedBody body, CancellationToken ct)
  {
    await cfgClient.SetOnvifArmedAsync(
      new SetOnvifArmedRequest { CameraId = cameraId, Armed = body.Subscribed }, Options(ct));
    return Ok();
  }

  public record SetSubscribedBody(bool Subscribed);

  [HttpPut("cameras/{cameraId}/poll-config")]
  public async Task<IActionResult> SetPollConfig(string cameraId, [FromBody] SetPollConfigBody body, CancellationToken ct)
  {
    if (body.PollTimeoutSeconds <= 0 || body.MessageLimit <= 0)
      return BadRequest("poll_timeout_seconds and message_limit must be positive");

    await cfgClient.SetOnvifPollConfigAsync(
      new SetOnvifPollConfigRequest
      {
        CameraId           = cameraId,
        PollTimeoutSeconds = body.PollTimeoutSeconds,
        MessageLimit       = body.MessageLimit,
      }, Options(ct));
    return Ok();
  }

  public record SetPollConfigBody(int PollTimeoutSeconds, int MessageLimit);

  // ── ONVIF debug events ────────────────────────────────────────────────────

  [HttpGet("recent-events")]
  // Collected from every node: each VmsOnvif only subscribes to its own cameras, so no single
  // instance holds the whole picture. A node that cannot be reached is skipped rather than failing
  // the request — this is a debug view, and a partial answer beats none.
  public async Task<IActionResult> GetAllRecentEvents(CancellationToken ct)
  {
    var perNode = await Task.WhenAll(registry.AllOnvifIds.Select(async onvifId =>
    {
      try
      {
        var client = new OnvifService.OnvifServiceClient(DaprClient.CreateInvocationInvoker(onvifId));
        var resp = await client.GetAllRecentOnvifEventsAsync(new GetAllRecentOnvifEventsRequest(), Options(ct));
        return resp.Events.AsEnumerable();
      }
      catch (RpcException ex)
      {
        logger.LogWarning(ex, "Recent ONVIF events unavailable from '{OnvifId}'", onvifId);
        return [];
      }
    }));

    return Ok(perNode
      .SelectMany(e => e)
      .OrderByDescending(e => e.ReceivedAt, StringComparer.Ordinal)
      .Select(e => new
      {
        cameraId   = e.CameraId,
        receivedAt = e.ReceivedAt,
        topic      = e.Topic,
        payloadXml = e.PayloadXml,
      }));
  }

  [HttpGet("cameras/{cameraId}/recent-events")]
  public async Task<IActionResult> GetRecentEvents(string cameraId, CancellationToken ct)
  {
    var resp = await (await router.OnvifAsync(cameraId, ct)).GetRecentOnvifEventsAsync(
      new GetRecentOnvifEventsRequest { CameraId = cameraId }, Options(ct));
    return Ok(resp.Events.Select(e => new
    {
      receivedAt = e.ReceivedAt,
      topic      = e.Topic,
      payloadXml = e.PayloadXml,
    }));
  }

  [HttpGet("cameras/{cameraId}/observed-topics")]
  public async Task<IActionResult> GetObservedTopics(string cameraId, CancellationToken ct)
  {
    var resp = await (await router.OnvifAsync(cameraId, ct)).GetObservedTopicsAsync(
      new GetObservedTopicsRequest { CameraId = cameraId }, Options(ct));
    return Ok(resp.Topics.Select(t => new
    {
      topic     = t.Topic,
      firstSeen = t.FirstSeen,
      lastSeen  = t.LastSeen,
    }));
  }
}

public record OnvifAlarmFilterBody(string? Id, string FilterText, int TimeoutSeconds, string? TextFilter);
