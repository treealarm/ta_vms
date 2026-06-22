using Dapr.Actors;
using Dapr.Actors.Client;
using Grpc.Core;
using OnvifLib;
using Vms.Onvif;
using web_vms.Types.Actors;

namespace VmsOnvif;

public class OnvifServiceImpl(
  CameraConnCache connCache,
  OnvifEventSubscriptionService subscriptionService,
  ObservedTopicsStore observedTopics,
  OnvifSettingsService settingsService,
  ILogger<OnvifServiceImpl> logger) : OnvifService.OnvifServiceBase
{
  public override async Task<GetProfilesResponse> GetProfiles(CameraRequest request, ServerCallContext context)
  {
    var data = await connCache.GetAsync(request.SourceId, context.CancellationToken);
    var response = new GetProfilesResponse();

    if (data?.Conn == null)
      return response;

    var actor = CameraActorProxy(request.SourceId);
    List<ProfileInfoDto> profiles;
    try
    {
      profiles = await actor.InvokeMethodAsync<CameraConnInfo, List<ProfileInfoDto>>(
        "GetProfilesAsync", data.Conn, context.CancellationToken);
    }
    catch (Exception ex)
    {
      throw new RpcException(new Status(StatusCode.Internal, $"Actor invoke failed: {ex.Message}"));
    }

    response.Profiles.AddRange(profiles.Select(p => ToOnvifProfile(p.Token, p.Name, p.Width, p.Height, p.Encoding)));
    return response;
  }

  // Used by the discovery wizard to preview a device's streams before the camera is
  // configured in VmsCfg — talks to the device directly, no CameraConnCache/actor lookup.
  public override async Task<GetProfilesResponse> GetProfilesByConnection(
    GetProfilesByConnectionRequest request, ServerCallContext context)
  {
    var response = new GetProfilesResponse();
    var camera = Camera.Create(request.Ip, request.Port, request.User, request.Pass, logger: new OnvifLoggerAdapter(logger), xaddr: request.Xaddr);
    await camera.InitTask;

    var mediaService = await camera.GetMediaService();
    if (mediaService == null)
      return response;

    response.Profiles.AddRange(mediaService.GetProfiles()
      .Select(p => ToOnvifProfile(p.Token, p.Name, p.Width, p.Height, p.Encoding)));
    return response;
  }

  public override async Task<GetSnapshotResponse> GetSnapshot(CameraRequest request, ServerCallContext context)
  {
    var data = await connCache.GetAsync(request.SourceId, context.CancellationToken);
    if (data?.Conn == null)
      return new GetSnapshotResponse { Error = "No ONVIF connection configured" };

    var actor = CameraActorProxy(request.SourceId);
    try
    {
      var result = await actor.InvokeMethodAsync<CameraConnInfo, SnapshotActorResult>(
        "GetSnapshotAsync", data.Conn, context.CancellationToken);
      if (result.Data.Length == 0)
        return new GetSnapshotResponse { Error = "Snapshot not available" };
      return new GetSnapshotResponse
      {
        Data     = Google.Protobuf.ByteString.CopyFrom(result.Data),
        MimeType = result.MimeType ?? string.Empty,
      };
    }
    catch (Exception ex)
    {
      return new GetSnapshotResponse { Error = ex.Message };
    }
  }

  private static OnvifProfile ToOnvifProfile(string token, string name, int width, int height, string encoding) => new()
  {
    Token = token,
    Name = name,
    Width = width,
    Height = height,
    Encoding = encoding
  };

  public override async Task<StreamUriResponse> GetStreamUri(StreamUriRequest request, ServerCallContext context)
  {
    var data = await connCache.GetAsync(request.SourceId, context.CancellationToken);

    if (data == null)
      return new StreamUriResponse();

    var fallbackUrl = data.Streams
      .FirstOrDefault(s => s.ProfileToken == request.ProfileToken)?.Path
      ?? data.Streams.FirstOrDefault()?.Path
      ?? string.Empty;

    if (data.Conn == null)
      return new StreamUriResponse { RtspUrl = fallbackUrl };

    var actor = CameraActorProxy(request.SourceId);
    GetStreamUriActorResult? result;
    try
    {
      result = await actor.InvokeMethodAsync<GetStreamUriActorRequest, GetStreamUriActorResult>(
        "GetStreamUriAsync", new GetStreamUriActorRequest(data.Conn, request.ProfileToken),
        context.CancellationToken);
    }
    catch (Exception ex)
    {
      // Degrade to the cached/configured path instead of failing the whole call —
      // a transient actor/placement hiccup must not take down live video when we
      // already have a usable URL on hand. But if there's nothing to fall back to,
      // surface the failure instead of silently returning an empty URL as if it succeeded.
      if (string.IsNullOrEmpty(fallbackUrl))
        throw new RpcException(new Status(StatusCode.Internal, $"Actor invoke failed: {ex.Message}"));

      logger.LogWarning(ex,
        "GetStreamUri actor invoke failed for camera '{CameraId}', falling back to cached path", request.SourceId);
      result = null;
    }

    return new StreamUriResponse
    {
      RtspUrl = string.IsNullOrEmpty(result?.Uri) ? fallbackUrl : result.Uri
    };
  }

  public override Task<GetRecentOnvifEventsResponse> GetRecentOnvifEvents(
    GetRecentOnvifEventsRequest request, ServerCallContext context)
  {
    var events = subscriptionService.GetRecentEvents(request.CameraId);
    var response = new GetRecentOnvifEventsResponse();
    response.Events.AddRange(events.Select(e => new OnvifEventRecord
    {
      ReceivedAt = e.ReceivedAt.ToString("O"),
      Topic      = e.Topic,
      PayloadXml = e.PayloadXml,
      CameraId   = request.CameraId,
    }));
    return Task.FromResult(response);
  }

  public override Task<GetAllRecentOnvifEventsResponse> GetAllRecentOnvifEvents(
    GetAllRecentOnvifEventsRequest request, ServerCallContext context)
  {
    var events = subscriptionService.GetAllRecentEvents();
    var response = new GetAllRecentOnvifEventsResponse();
    response.Events.AddRange(events.Select(e => new OnvifEventRecord
    {
      ReceivedAt = e.Entry.ReceivedAt.ToString("O"),
      Topic      = e.Entry.Topic,
      PayloadXml = e.Entry.PayloadXml,
      CameraId   = e.CameraId,
    }));
    return Task.FromResult(response);
  }

  public override Task<GetObservedTopicsResponse> GetObservedTopics(
    GetObservedTopicsRequest request, ServerCallContext context)
  {
    var topics = observedTopics.GetTopics(request.CameraId);
    var response = new GetObservedTopicsResponse();
    response.Topics.AddRange(topics.Select(t => new ObservedTopicEntry
    {
      Topic     = t.Topic,
      FirstSeen = t.FirstSeen.ToString("O"),
      LastSeen  = t.LastSeen.ToString("O"),
    }));
    return Task.FromResult(response);
  }

  public override async Task<GetVideoEncoderConfigsResponse> GetVideoEncoderConfigs(
    CameraRequest request, ServerCallContext context)
  {
    List<OnvifLib.OnvifVideoEncoderConfig> configs;
    try
    {
      configs = await settingsService.GetConfigsAsync(request.SourceId, context.CancellationToken);
    }
    catch (Exception ex)
    {
      throw new RpcException(new Status(StatusCode.Internal, ex.Message));
    }

    var response = new GetVideoEncoderConfigsResponse();
    response.Configs.AddRange(configs.Select(c => new VideoEncoderConfig
    {
      Token          = c.Token,
      Name           = c.Name,
      Encoding       = c.Encoding,
      Width          = c.Width,
      Height         = c.Height,
      FrameRateLimit = c.FrameRateLimit,
      BitrateLimit   = c.BitrateLimit,
      GovLength      = c.GovLength,
      H264Profile    = c.H264Profile,
      Quality        = c.Quality,
    }));
    return response;
  }

  public override async Task<VideoEncoderConfigOptionsResponse> GetVideoEncoderConfigOptions(
    VideoEncoderConfigOptionsRequest request, ServerCallContext context)
  {
    List<OnvifLib.OnvifVideoEncoderOptions> opts;
    try
    {
      opts = await settingsService.GetConfigOptionsAsync(
        request.SourceId, request.ConfigToken, context.CancellationToken);
    }
    catch (Exception ex)
    {
      throw new RpcException(new Status(StatusCode.Internal, ex.Message));
    }

    var response = new VideoEncoderConfigOptionsResponse();
    response.Options.AddRange(opts.Select(o =>
    {
      var entry = new VideoEncoderConfigOptions
      {
        Encoding     = o.Encoding,
        MinFrameRate = o.MinFrameRate,
        MaxFrameRate = o.MaxFrameRate,
        MinBitrate   = o.MinBitrate,
        MaxBitrate   = o.MaxBitrate,
        MinGovLength = o.MinGovLength,
        MaxGovLength = o.MaxGovLength,
      };
      entry.Resolutions.AddRange(o.Resolutions.Select(r => new ResolutionOption { Width = r.Width, Height = r.Height }));
      entry.H264Profiles.AddRange(o.H264Profiles);
      return entry;
    }));
    return response;
  }

  public override async Task<SetVideoEncoderConfigResponse> SetVideoEncoderConfig(
    SetVideoEncoderConfigRequest request, ServerCallContext context)
  {
    if (request.Config == null)
      return new SetVideoEncoderConfigResponse { Success = false, Error = "Config is required" };

    try
    {
      var cfg = new OnvifLib.OnvifVideoEncoderConfig(
        Token:          request.Config.Token,
        Name:           request.Config.Name,
        Encoding:       request.Config.Encoding,
        Width:          request.Config.Width,
        Height:         request.Config.Height,
        FrameRateLimit: request.Config.FrameRateLimit,
        BitrateLimit:   request.Config.BitrateLimit,
        GovLength:      request.Config.GovLength,
        H264Profile:    request.Config.H264Profile,
        Quality:        request.Config.Quality);

      await settingsService.SetConfigAsync(request.SourceId, cfg, context.CancellationToken);
      return new SetVideoEncoderConfigResponse { Success = true };
    }
    catch (Exception ex)
    {
      return new SetVideoEncoderConfigResponse { Success = false, Error = ex.Message };
    }
  }

  // ── Audio encoder ────────────────────────────────────────────────────────

  public override async Task<GetAudioEncoderConfigsResponse> GetAudioEncoderConfigs(
    CameraRequest request, ServerCallContext context)
  {
    List<OnvifLib.OnvifAudioEncoderConfig> configs;
    try
    {
      configs = await settingsService.GetAudioConfigsAsync(request.SourceId, context.CancellationToken);
    }
    catch (Exception ex)
    {
      throw new RpcException(new Status(StatusCode.Internal, ex.Message));
    }
    var response = new GetAudioEncoderConfigsResponse();
    response.Configs.AddRange(configs.Select(c => new AudioEncoderConfig
    {
      Token      = c.Token,
      Name       = c.Name,
      Encoding   = c.Encoding,
      Bitrate    = c.Bitrate,
      SampleRate = c.SampleRate,
    }));
    return response;
  }

  public override async Task<AudioEncoderConfigOptionsResponse> GetAudioEncoderConfigOptions(
    AudioEncoderConfigOptionsRequest request, ServerCallContext context)
  {
    List<OnvifLib.OnvifAudioEncoderOption> opts;
    try
    {
      opts = await settingsService.GetAudioConfigOptionsAsync(
        request.SourceId, request.ConfigToken, context.CancellationToken);
    }
    catch (Exception ex)
    {
      throw new RpcException(new Status(StatusCode.Internal, ex.Message));
    }
    var response = new AudioEncoderConfigOptionsResponse();
    response.Options.AddRange(opts.Select(o =>
    {
      var entry = new AudioEncoderConfigOptions { Encoding = o.Encoding };
      entry.Bitrates.AddRange(o.Bitrates);
      entry.SampleRates.AddRange(o.SampleRates);
      return entry;
    }));
    return response;
  }

  public override async Task<SetAudioEncoderConfigResponse> SetAudioEncoderConfig(
    SetAudioEncoderConfigRequest request, ServerCallContext context)
  {
    if (request.Config == null)
      return new SetAudioEncoderConfigResponse { Success = false, Error = "Config is required" };
    try
    {
      var cfg = new OnvifLib.OnvifAudioEncoderConfig(
        Token:      request.Config.Token,
        Name:       request.Config.Name,
        Encoding:   request.Config.Encoding,
        Bitrate:    request.Config.Bitrate,
        SampleRate: request.Config.SampleRate);
      await settingsService.SetAudioConfigAsync(request.SourceId, cfg, context.CancellationToken);
      return new SetAudioEncoderConfigResponse { Success = true };
    }
    catch (Exception ex)
    {
      return new SetAudioEncoderConfigResponse { Success = false, Error = ex.Message };
    }
  }

  // ── System ───────────────────────────────────────────────────────────────

  public override async Task<GetDeviceTimeResponse> GetDeviceTime(
    CameraRequest request, ServerCallContext context)
  {
    var time = await settingsService.GetDeviceTimeAsync(request.SourceId, context.CancellationToken);
    return new GetDeviceTimeResponse
    {
      UtcTime = time.HasValue ? time.Value.ToString("O") : string.Empty,
    };
  }

  public override async Task<SyncDeviceTimeResponse> SyncDeviceTime(
    CameraRequest request, ServerCallContext context)
  {
    try
    {
      await settingsService.SyncDeviceTimeAsync(request.SourceId, context.CancellationToken);
      return new SyncDeviceTimeResponse { Success = true };
    }
    catch (Exception ex)
    {
      return new SyncDeviceTimeResponse { Success = false, Error = ex.Message };
    }
  }

  public override async Task<SyncDeviceTimeResponse> SetDeviceTime(
    SetDeviceTimeRequest request, ServerCallContext context)
  {
    try
    {
      if (!DateTimeOffset.TryParse(request.UtcTime, out var dto))
        return new SyncDeviceTimeResponse { Success = false, Error = "Invalid UTC time format" };
      await settingsService.SetDeviceTimeAsync(request.SourceId, dto.UtcDateTime, context.CancellationToken);
      return new SyncDeviceTimeResponse { Success = true };
    }
    catch (Exception ex)
    {
      return new SyncDeviceTimeResponse { Success = false, Error = ex.Message };
    }
  }

  public override async Task<GetDeviceInformationResponse> GetDeviceInformation(
    CameraRequest request, ServerCallContext context)
  {
    var info = await settingsService.GetDeviceInfoAsync(request.SourceId, context.CancellationToken);
    if (info == null)
      return new GetDeviceInformationResponse { Error = "Device information unavailable" };

    return new GetDeviceInformationResponse
    {
      Manufacturer    = info.Manufacturer,
      Model           = info.Model,
      FirmwareVersion = info.FirmwareVersion,
      SerialNumber    = info.SerialNumber,
      HardwareId      = info.HardwareId,
    };
  }

  public override async Task<GetDeviceCapabilitiesResponse> GetDeviceCapabilities(
    CameraRequest request, ServerCallContext context)
  {
    var capsTask = settingsService.GetCapabilitiesAsync(request.SourceId, context.CancellationToken);
    var audioConfigsTask = settingsService.GetAudioConfigsAsync(request.SourceId, context.CancellationToken);
    await Task.WhenAll(capsTask, audioConfigsTask);
    var caps = capsTask.Result;
    var audioConfigs = audioConfigsTask.Result;

    return new GetDeviceCapabilitiesResponse
    {
      HasPtz     = caps?.HasPtz ?? false,
      HasImaging = caps?.HasImaging ?? false,
      HasEvents  = caps?.HasEvents ?? false,
      HasAudio   = audioConfigs.Count > 0,
    };
  }

  // ── Imaging ──────────────────────────────────────────────────────────────

  // video_source_token is usually unknown to the caller — resolve it server-side from the
  // camera's first media profile so the frontend doesn't need to know about video sources.
  private async Task<string> ResolveVideoSourceTokenAsync(string cameraId, string requestedToken, CancellationToken ct)
  {
    if (!string.IsNullOrEmpty(requestedToken))
      return requestedToken;

    var data = await connCache.GetAsync(cameraId, ct);
    if (data?.Conn == null)
      return string.Empty;

    try
    {
      var actor = CameraActorProxy(cameraId);
      var profiles = await actor.InvokeMethodAsync<CameraConnInfo, List<ProfileInfoDto>>(
        "GetProfilesAsync", data.Conn, ct);
      return profiles.FirstOrDefault()?.VideoSourceToken ?? string.Empty;
    }
    catch (Exception ex)
    {
      logger.LogWarning(ex, "Failed to resolve video source token for camera {CameraId}", cameraId);
      return string.Empty;
    }
  }

  public override async Task<ImagingSettingsResponse> GetImagingSettings(
    ImagingSettingsRequest request, ServerCallContext context)
  {
    var token = await ResolveVideoSourceTokenAsync(request.SourceId, request.VideoSourceToken, context.CancellationToken);
    if (string.IsNullOrEmpty(token))
      return new ImagingSettingsResponse { Error = "No video source available" };

    OnvifLib.OnvifImagingSettings? settings;
    try
    {
      settings = await settingsService.GetImagingSettingsAsync(request.SourceId, token, context.CancellationToken);
    }
    catch (Exception ex)
    {
      return new ImagingSettingsResponse { Error = ex.Message };
    }
    if (settings == null)
      return new ImagingSettingsResponse { Error = "Imaging settings unavailable" };

    var response = new ImagingSettingsResponse();
    if (settings.Brightness.HasValue) response.Brightness = settings.Brightness.Value;
    if (settings.Contrast.HasValue) response.Contrast = settings.Contrast.Value;
    if (settings.ColorSaturation.HasValue) response.ColorSaturation = settings.ColorSaturation.Value;
    if (settings.Sharpness.HasValue) response.Sharpness = settings.Sharpness.Value;
    return response;
  }

  public override async Task<ImagingOptionsResponse> GetImagingOptions(
    ImagingSettingsRequest request, ServerCallContext context)
  {
    var token = await ResolveVideoSourceTokenAsync(request.SourceId, request.VideoSourceToken, context.CancellationToken);
    if (string.IsNullOrEmpty(token))
      return new ImagingOptionsResponse { Error = "No video source available" };

    OnvifLib.OnvifImagingOptions? options;
    try
    {
      options = await settingsService.GetImagingOptionsAsync(request.SourceId, token, context.CancellationToken);
    }
    catch (Exception ex)
    {
      return new ImagingOptionsResponse { Error = ex.Message };
    }
    if (options == null)
      return new ImagingOptionsResponse { Error = "Imaging options unavailable" };

    var response = new ImagingOptionsResponse();
    if (options.Brightness != null) response.Brightness = new FloatRange { Min = options.Brightness.Min, Max = options.Brightness.Max };
    if (options.Contrast != null) response.Contrast = new FloatRange { Min = options.Contrast.Min, Max = options.Contrast.Max };
    if (options.ColorSaturation != null) response.ColorSaturation = new FloatRange { Min = options.ColorSaturation.Min, Max = options.ColorSaturation.Max };
    if (options.Sharpness != null) response.Sharpness = new FloatRange { Min = options.Sharpness.Min, Max = options.Sharpness.Max };
    return response;
  }

  public override async Task<SetImagingSettingsResponse> SetImagingSettings(
    SetImagingSettingsRequest request, ServerCallContext context)
  {
    var token = await ResolveVideoSourceTokenAsync(request.SourceId, request.VideoSourceToken, context.CancellationToken);
    if (string.IsNullOrEmpty(token))
      return new SetImagingSettingsResponse { Success = false, Error = "No video source available" };

    try
    {
      var settings = new OnvifLib.OnvifImagingSettings(
        request.HasBrightness ? request.Brightness : null,
        request.HasContrast ? request.Contrast : null,
        request.HasColorSaturation ? request.ColorSaturation : null,
        request.HasSharpness ? request.Sharpness : null);

      await settingsService.SetImagingSettingsAsync(request.SourceId, token, settings, context.CancellationToken);
      return new SetImagingSettingsResponse { Success = true };
    }
    catch (Exception ex)
    {
      return new SetImagingSettingsResponse { Success = false, Error = ex.Message };
    }
  }

  public override async Task<RebootDeviceResponse> RebootDevice(
    CameraRequest request, ServerCallContext context)
  {
    try
    {
      var msg = await settingsService.RebootDeviceAsync(request.SourceId, context.CancellationToken);
      return new RebootDeviceResponse { Success = true, Message = msg ?? string.Empty };
    }
    catch (Exception ex)
    {
      return new RebootDeviceResponse { Success = false, Message = ex.Message };
    }
  }

  public override async Task<DiscoverDevicesResponse> DiscoverDevices(
    DiscoverDevicesRequest request, ServerCallContext context)
  {
    var timeout = request.TimeoutSeconds > 0
      ? TimeSpan.FromSeconds(request.TimeoutSeconds)
      : TimeSpan.FromSeconds(4);

    var probeResult = await WsDiscovery.ProbeAsync(
      timeout, context.CancellationToken, new OnvifLoggerAdapter(logger));

    var response = new DiscoverDevicesResponse { ScanOk = probeResult.ScanOk };
    foreach (var d in probeResult.Devices)
    {
      var proto = new Vms.Onvif.DiscoveredDevice
      {
        Ip = d.Ip,
        Port = d.Port,
        EndpointUuid = d.EndpointUuid,
        Hardware = d.Hardware ?? string.Empty,
        Name = d.Name ?? string.Empty,
      };
      proto.Xaddrs.AddRange(d.XAddrs);
      response.Devices.Add(proto);
    }
    return response;
  }

  // Non-remoting proxy: uses JSON serialization, no DataContractSerializer
  private static ActorProxy CameraActorProxy(string cameraId) =>
    ActorProxy.Create(new ActorId(cameraId), "CameraActor");
}
