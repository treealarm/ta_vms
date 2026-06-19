using Grpc.Core;
using Vms.Cfg;
using web_vms.Types.Dto;

namespace web_vms;

public class CameraManager : ICameraManager
{
  private static readonly TimeSpan CfgCallTimeout = TimeSpan.FromSeconds(5);

  private readonly CfgService.CfgServiceClient _cfgClient;

  public CameraManager(CfgService.CfgServiceClient cfgClient)
  {
    _cfgClient = cfgClient;
  }

  private static CallOptions CfgOptions(CancellationToken ct = default) =>
    new(deadline: DateTime.UtcNow.Add(CfgCallTimeout), cancellationToken: ct);

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
      })
      .ToList();

    var firstStream = streams.FirstOrDefault();
    var effectiveProfile = firstStream?.OverrideProfile ?? firstStream?.RecordingProfile;
    var mode = effectiveProfile?.Mode ?? "off";

    return new CameraSetupDto
    {
      CameraId         = setup.CameraId,
      Name             = setup.Name,
      ServerId         = setup.ServerId,
      RecordingMode    = mode,
      Recording        = mode is "continuous" or "prealarm",
      WebRtc           = firstStream?.WebRtc ?? false,
      Path             = firstStream?.Path,
      Streams          = streams,
      Cred             = setup.Cred == null ? null : new CredentialsDto { User = setup.Cred.User, Pass = setup.Cred.Pass },
      OnvifConn        = setup.OnvifConn == null ? null : new OnvifConnectionDto { ip = setup.OnvifConn.Ip, port = setup.OnvifConn.Port },
      MediaMtxServerId    = setup.HasMediaMtxServerId ? setup.MediaMtxServerId : null,
      OnvifEventsEnabled  = setup.OnvifEventsEnabled,
      HasOnvifFilters     = setup.HasOnvifFilters,
      OnvifManaged        = setup.OnvifManaged,
      Disabled            = setup.HasDisabled && setup.Disabled ? true : null,
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
      ServerId         = dto.ServerId ?? string.Empty,
      Cred             = dto.Cred == null ? null : new Credentials { User = dto.Cred.User, Pass = dto.Cred.Pass },
      OnvifConn        = dto.OnvifConn == null ? null : new OnvifConnection { Ip = dto.OnvifConn.ip, Port = dto.OnvifConn.port },
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
