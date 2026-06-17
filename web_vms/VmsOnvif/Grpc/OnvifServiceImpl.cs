using Dapr.Actors;
using Dapr.Actors.Client;
using Grpc.Core;
using Vms.Onvif;
using web_vms.Types.Actors;

namespace VmsOnvif;

public class OnvifServiceImpl(
  CameraConnCache connCache,
  OnvifEventSubscriptionService subscriptionService,
  ObservedTopicsStore observedTopics,
  OnvifSettingsService settingsService) : OnvifService.OnvifServiceBase
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

    response.Profiles.AddRange(profiles.Select(p => new OnvifProfile
    {
      Token = p.Token,
      Name = p.Name,
      Width = p.Width,
      Height = p.Height,
      Encoding = p.Encoding
    }));
    return response;
  }

  public override async Task<StreamUriResponse> GetStreamUri(StreamUriRequest request, ServerCallContext context)
  {
    var data = await connCache.GetAsync(request.SourceId, context.CancellationToken);

    if (data == null)
      return new StreamUriResponse();

    var fallbackUrl = data.Streams
      .FirstOrDefault(s => s.ProfileToken == request.ProfileToken)?.RtspUrl
      ?? data.Streams.FirstOrDefault()?.RtspUrl
      ?? string.Empty;

    if (data.Conn == null)
      return new StreamUriResponse { RtspUrl = fallbackUrl };

    var actor = CameraActorProxy(request.SourceId);
    GetStreamUriActorResult result;
    try
    {
      result = await actor.InvokeMethodAsync<GetStreamUriActorRequest, GetStreamUriActorResult>(
        "GetStreamUriAsync", new GetStreamUriActorRequest(data.Conn, request.ProfileToken),
        context.CancellationToken);
    }
    catch (Exception ex)
    {
      throw new RpcException(new Status(StatusCode.Internal, $"Actor invoke failed: {ex.Message}"));
    }

    return new StreamUriResponse
    {
      RtspUrl = string.IsNullOrEmpty(result.Uri) ? fallbackUrl : result.Uri
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

  // Non-remoting proxy: uses JSON serialization, no DataContractSerializer
  private static ActorProxy CameraActorProxy(string cameraId) =>
    ActorProxy.Create(new ActorId(cameraId), "CameraActor");
}
