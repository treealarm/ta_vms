using Grpc.Core;
using Vms.Cfg;
using web_vms.Types.Dto;
using web_vms.Types.Recording;

namespace web_vms;

public class CameraManager : ICameraManager
{
  private static readonly TimeSpan CfgCallTimeout = TimeSpan.FromSeconds(5);

  private readonly CfgService.CfgServiceClient _cfgClient;
  private readonly ILogger<CameraManager> _logger;

  public CameraManager(CfgService.CfgServiceClient cfgClient, ILogger<CameraManager> logger)
  {
    _cfgClient = cfgClient;
    _logger = logger;
  }

  private static CallOptions CfgOptions(CancellationToken ct = default) =>
    new(deadline: DateTime.UtcNow.Add(CfgCallTimeout), cancellationToken: ct);

  // Short-lived cache of the enabled-analytics-watch camera set. It gates the live bbox overlay
  // toggle and is read on the per-camera state poll (every 5s per open card) as well as the camera
  // list — reading it uncached would hammer VmsCfg with a config lookup that rarely changes, which
  // CLAUDE.md forbids on hot paths. A refresh failure keeps the last good set instead of throwing,
  // so a VmsCfg/analytics hiccup never fails the core camera list/state endpoints.
  private static readonly TimeSpan AnalyticsCacheTtl = TimeSpan.FromSeconds(15);
  private readonly SemaphoreSlim _analyticsRefreshLock = new(1, 1);
  private volatile HashSet<string> _analyticsEnabled = new();
  private DateTime _analyticsCachedAtUtc = DateTime.MinValue;

  private async Task<HashSet<string>> GetAnalyticsEnabledAsync(CancellationToken ct)
  {
    if (DateTime.UtcNow - _analyticsCachedAtUtc < AnalyticsCacheTtl)
      return _analyticsEnabled;

    await _analyticsRefreshLock.WaitAsync(ct);
    try
    {
      if (DateTime.UtcNow - _analyticsCachedAtUtc < AnalyticsCacheTtl)
        return _analyticsEnabled;

      var response = await _cfgClient.ListAnalyticsWatchesAsync(new ListAnalyticsWatchesRequest(), CfgOptions(ct));
      _analyticsEnabled = response.Watches.Where(w => w.Enabled).Select(w => w.CameraId).ToHashSet();
      _analyticsCachedAtUtc = DateTime.UtcNow;
    }
    catch (Exception ex)
    {
      // Keep serving the last-known set — the overlay flag is not worth failing a camera query over.
      _logger.LogWarning(ex, "Failed to refresh analytics-watch cache; serving stale set");
    }
    finally
    {
      _analyticsRefreshLock.Release();
    }
    return _analyticsEnabled;
  }

  public async Task<bool> Add(CameraSetupDto dto, CancellationToken ct = default)
  {
    var response = await _cfgClient.SetupCameraAsync(ToSetupProto(dto), CfgOptions(ct));
    return response.Success;
  }

  public async Task<bool> Remove(string id, CancellationToken ct = default)
  {
    var response = await _cfgClient.RemoveCameraAsync(new SourceRequest { SourceId = id }, CfgOptions(ct));
    return response.Success;
  }

  public async Task<List<CameraSetupDto>> List(CancellationToken ct = default)
  {
    var response = await _cfgClient.ListCamerasAsync(new ListCamerasRequest(), CfgOptions(ct));
    return response.Cameras.Select(ToDto).ToList();
  }

  public async Task<HashSet<string>> AnalyticsEnabledCameraIds(CancellationToken ct = default) =>
    await GetAnalyticsEnabledAsync(ct);

  public async Task<bool> HasEnabledAnalyticsWatch(string cameraId, CancellationToken ct = default) =>
    (await GetAnalyticsEnabledAsync(ct)).Contains(cameraId);

  public async Task<List<MediaServerNodeDto>> ListServers(CancellationToken ct = default)
  {
    var response = await _cfgClient.ListMediaServersAsync(new ListMediaServersRequest(), CfgOptions(ct));
    return response.Servers.Select(ToDto).ToList();
  }

  public async Task<bool> SetSetup(CameraSetupDto dto, CancellationToken ct = default)
  {
    var response = await _cfgClient.SetupCameraAsync(ToSetupProto(dto), CfgOptions(ct));
    return response.Success;
  }

  public async Task<CameraSetupDto?> Get(string id, CancellationToken ct = default)
  {
    var response = await _cfgClient.GetCameraAsync(new SourceRequest { SourceId = id }, CfgOptions(ct));
    return response.Found ? ToDto(response.Setup) : null;
  }

  public static CameraSetupDto Normalize(CameraSetupDto dto) => dto;

  private static CameraSetupDto ToDto(CameraSetup setup)
  {
    var streams = setup.Streams
      .Select(s => new CameraStreamDto
      {
        StreamName       = s.StreamName,
        ProfileToken     = s.ProfileToken,
        Path             = s.Path,
        WebRtc           = s.WebRtc,
        RecordingProfile = s.RecordingProfile != null ? ToProfileDto(s.RecordingProfile) : null,
        OverrideProfile  = s.OverrideProfile  != null ? ToProfileDto(s.OverrideProfile)  : null,
        Disabled         = s.HasDisabled && s.Disabled ? true : null,
        Recordable       = !s.HasRecordable || s.Recordable,
        DeclaredWidth    = s.DeclaredWidth,
        DeclaredHeight   = s.DeclaredHeight,
        Stats            = s.Stats is null ? null : new StreamStatsDto
        {
          Width        = s.Stats.Width,
          Height       = s.Stats.Height,
          Codec        = s.Stats.Codec,
          Fps          = s.Stats.Fps,
          BitrateBps   = s.Stats.BitrateBps,
          ObservedAtMs = s.Stats.ObservedAtMs,
          TranscodeEncoder = s.Stats.TranscodeEncoder,
        },
      })
      .ToList();

    var firstStream = streams.FirstOrDefault();

    // The camera's own recording state, across every enabled stream that may be recorded — not the
    // first stream's. The first stream is whichever one sorts first, and once a stream can be
    // marked "not available for recording" it can easily be one that never records while another
    // does; the card would then say the camera is idle while footage is being written.
    var streamModes = streams
      .Where(s => s.Disabled != true && s.Recordable)
      .Select(s => RecordingRules.Effective(
        setup.ScheduleActive,
        recordable: true,
        s.OverrideProfile is null ? null : RecordingRules.Parse(s.OverrideProfile.Mode),
        s.RecordingProfile is null ? null : RecordingRules.Parse(s.RecordingProfile.Mode)))
      .ToList();

    var mode = RecordingRules.ToWire(
      streamModes.Contains(RecordingModeKind.Continuous) ? RecordingModeKind.Continuous
      : streamModes.Contains(RecordingModeKind.Prealarm) ? RecordingModeKind.Prealarm
      : RecordingModeKind.Off);

    return new CameraSetupDto
    {
      CameraId         = setup.CameraId,
      Name             = setup.Name,
      EdgeBackfillStream = setup.EdgeBackfillStream,
      RoiTranscode       = setup.RoiTranscode,
      RoiDelayHours      = setup.RoiDelayHours,
      RoiMinCoverage     = setup.RoiMinCoverage,
      RoiMaxCoverage     = setup.RoiMaxCoverage,
      OnvifMetadataDisabled = setup.OnvifMetadataDisabled,
      HasTelemetry = setup.HasTelemetry,
      ServerId         = setup.ServerId,
      RecordingMode    = mode,
      Recording        = mode is "continuous" or "prealarm",
      WebRtc           = firstStream?.WebRtc ?? false,
      Path             = firstStream?.Path,
      Streams          = streams,
      Cred             = setup.Cred == null ? null : new CredentialsDto { User = setup.Cred.User, Pass = setup.Cred.Pass },
      OnvifConn        = setup.OnvifConn == null ? null : new OnvifConnectionDto { ip = setup.OnvifConn.Ip, port = setup.OnvifConn.Port, xaddr = setup.OnvifConn.Xaddr },
      MediaMtxServerId    = setup.HasMediaMtxServerId ? setup.MediaMtxServerId : null,
      EffectiveWebrtcStunUrl = setup.HasEffectiveWebrtcStunUrl ? setup.EffectiveWebrtcStunUrl : null,
      OnvifEventsEnabled  = setup.OnvifEventsEnabled,
      HasOnvifFilters     = setup.HasOnvifFilters,
      OnvifManaged        = setup.OnvifManaged,
      Disabled            = setup.HasDisabled && setup.Disabled ? true : null,
      ScheduleIds         = setup.ScheduleIds.ToList(),
      ScheduleActive      = setup.ScheduleActive,
    };
  }

  private static RecordingProfileDto ToProfileDto(RecordingProfile p) => new()
  {
    Id                = p.Id,
    Name              = p.Name,
    Mode              = RecordingModeToString(p.Mode),
    PreRecordSeconds  = p.PreRecordSeconds,
    PostRecordSeconds = p.PostRecordSeconds,
    SegmentSeconds    = p.SegmentSeconds,
    IsOverridePreset  = p.IsOverridePreset,
    IsBuiltin         = p.IsBuiltin,
  };

  public async Task<bool?> GetArmed(string cameraId, CancellationToken ct = default)
  {
    try
    {
      var resp = await _cfgClient.GetCamStateAsync(
        new GetCamStateRequest { CameraId = cameraId }, CfgOptions(ct));
      return resp.Armed;
    }
    catch (RpcException ex)
    {
      // Reported as unknown rather than as "not armed": this rides along with the state poll, and a
      // hiccup here must not make the card claim the camera is disarmed.
      _logger.LogWarning("Reading the armed flag for '{CameraId}' failed: {Status}", cameraId, ex.StatusCode);
      return null;
    }
  }

  public async Task<List<MediaMtxServerDto>> ListMediaMtxServers(CancellationToken ct = default)
  {
    var response = await _cfgClient.ListMediaMtxServersAsync(new ListMediaMtxServersRequest(), CfgOptions(ct));
    return response.Servers.Select(ToMediaMtxDto).ToList();
  }

  public async Task<bool> UpsertMediaMtxServer(MediaMtxServerDto dto, CancellationToken ct = default)
  {
    var response = await _cfgClient.UpsertMediaMtxServerAsync(ToMediaMtxProto(dto), CfgOptions(ct));
    return response.Success;
  }

  public async Task<bool> RemoveMediaMtxServer(string id, CancellationToken ct = default)
  {
    var response = await _cfgClient.RemoveMediaMtxServerAsync(new MediaMtxServerRequest { Id = id }, CfgOptions(ct));
    return response.Success;
  }

  public async Task<MediaMtxServerDto?> GetOrAssignMediaMtxServer(string cameraId, CancellationToken ct = default)
  {
    var response = await _cfgClient.GetOrAssignMediaMtxServerAsync(new SourceRequest { SourceId = cameraId }, CfgOptions(ct));
    return response.Found ? ToMediaMtxDto(response.Server) : null;
  }

  private static MediaMtxServerDto ToMediaMtxDto(MediaMtxServer s) => new()
  {
    Id         = s.Id,
    Name       = s.Name,
    Host       = s.Host,
    WebrtcHost = s.WebrtcHost,
    RtspPort   = s.RtspPort != 0 ? s.RtspPort : 8554,
    WebrtcPort = s.WebrtcPort != 0 ? s.WebrtcPort : 8889,
    ApiPort    = s.ApiPort != 0 ? s.ApiPort : 9997,
    StunUrl    = s.StunUrl,
    ServerId   = s.ServerId,
    PublishUser = s.PublishUser,
    PublishPass = s.PublishPass,
    ReadUser    = s.ReadUser,
    ReadPass    = s.ReadPass,
    TranscodeHevc = s.TranscodeHevc,
  };

  private static MediaMtxServer ToMediaMtxProto(MediaMtxServerDto dto) => new()
  {
    Id         = dto.Id ?? string.Empty,
    Name       = dto.Name,
    Host       = dto.Host,
    WebrtcHost = dto.WebrtcHost,
    RtspPort   = dto.RtspPort,
    WebrtcPort = dto.WebrtcPort,
    ApiPort    = dto.ApiPort,
    StunUrl    = dto.StunUrl ?? string.Empty,
    ServerId   = dto.ServerId ?? string.Empty,
    PublishUser = dto.PublishUser ?? string.Empty,
    PublishPass = dto.PublishPass ?? string.Empty,
    ReadUser    = dto.ReadUser ?? string.Empty,
    ReadPass    = dto.ReadPass ?? string.Empty,
    TranscodeHevc = dto.TranscodeHevc,
  };

  private static MediaServerNodeDto ToDto(MediaServerNode server) => new()
  {
    ServerId = server.ServerId,
    Status   = server.Status,
    FsId     = server.FsId,
  };

  private static CameraSetup ToSetupProto(CameraSetupDto dto)
  {
    var setup = new CameraSetup
    {
      CameraId         = dto.CameraId,
      Name             = dto.Name,
      EdgeBackfillStream = dto.EdgeBackfillStream ?? string.Empty,
      RoiTranscode       = dto.RoiTranscode,
      RoiDelayHours      = dto.RoiDelayHours,
      RoiMinCoverage     = dto.RoiMinCoverage,
      RoiMaxCoverage     = dto.RoiMaxCoverage,
      OnvifMetadataDisabled = dto.OnvifMetadataDisabled,
      HasTelemetry = dto.HasTelemetry,
      ServerId         = dto.ServerId ?? string.Empty,
      Cred             = dto.Cred == null ? null : new Credentials { User = dto.Cred.User, Pass = dto.Cred.Pass },
      OnvifConn        = dto.OnvifConn == null ? null : new OnvifConnection { Ip = dto.OnvifConn.ip, Port = dto.OnvifConn.port, Xaddr = dto.OnvifConn.xaddr ?? string.Empty },
      MediaMtxServerId   = dto.MediaMtxServerId ?? string.Empty,
      OnvifEventsEnabled = dto.OnvifEventsEnabled,
      Disabled           = dto.Disabled == true,
    };

    if (dto.Streams.Count > 0)
    {
      setup.Streams.AddRange(dto.Streams.Select(s =>
      {
        var stream = new CameraStream
        {
          StreamName   = s.StreamName,
          ProfileToken = s.ProfileToken,
          Path         = s.Path,
          WebRtc       = s.WebRtc,
          Disabled     = s.Disabled == true,
          // Always set: this method re-sends the whole setup on every stream edit, so leaving it
          // unset would silently take every stream out of the archive.
          Recordable   = s.Recordable,
          // Zero is "not known here" and VmsCfg keeps whatever it already has; only a caller that
          // actually asked ONVIF sends a value.
          DeclaredWidth  = s.DeclaredWidth,
          DeclaredHeight = s.DeclaredHeight,
        };
        if (s.RecordingProfile != null)
          stream.RecordingProfile = ToProfileProto(s.RecordingProfile);
        return stream;
      }));
    }
    else if (!string.IsNullOrEmpty(dto.Path))
    {
      setup.Streams.Add(new CameraStream
      {
        StreamName = "main",
        Path       = dto.Path,
        WebRtc     = dto.WebRtc,
        Recordable = true,
      });
    }

    return setup;
  }

  private static RecordingProfile ToProfileProto(RecordingProfileDto p) => new()
  {
    Id                = p.Id,
    Name              = p.Name,
    Mode              = ParseRecordingMode(p.Mode),
    PreRecordSeconds  = p.PreRecordSeconds,
    PostRecordSeconds = p.PostRecordSeconds,
    SegmentSeconds    = p.SegmentSeconds,
  };

  private static string RecordingModeToString(RecordingMode mode) => mode switch
  {
    RecordingMode.RecordingContinuous => "continuous",
    RecordingMode.RecordingPrealarm   => "prealarm",
    _                                 => "off",
  };

  private static RecordingMode ParseRecordingMode(string mode) => mode switch
  {
    "continuous" => RecordingMode.RecordingContinuous,
    "prealarm"   => RecordingMode.RecordingPrealarm,
    _            => RecordingMode.RecordingOff,
  };
}
