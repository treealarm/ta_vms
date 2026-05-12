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
        RtspUrl          = s.RtspUrl,
        WebRtc           = s.WebRtc,
        RecordingProfile = s.RecordingProfile != null ? ToProfileDto(s.RecordingProfile) : null,
      })
      .ToList();

    var firstStream = streams.FirstOrDefault();
    var mode = firstStream?.RecordingProfile?.Mode ?? "off";

    return new CameraSetupDto
    {
      CameraId      = setup.CameraId,
      ServerId      = setup.ServerId,
      RecordingMode = mode,
      Recording     = mode is "continuous" or "prealarm",
      WebRtc        = firstStream?.WebRtc ?? false,
      RtspUrl       = firstStream?.RtspUrl,
      Streams       = streams,
      Cred          = setup.Cred == null ? null : new CredentialsDto { User = setup.Cred.User, Pass = setup.Cred.Pass },
      OnvifConn     = setup.OnvifConn == null ? null : new OnvifConnectionDto { ip = setup.OnvifConn.Ip, port = setup.OnvifConn.Port },
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
  };

  private static MediaServerNodeDto ToDto(MediaServerNode server) => new()
  {
    ServerId = server.ServerId,
    Status   = server.Status
  };

  private static CameraSetup ToSetupProto(CameraSetupDto dto)
  {
    var setup = new CameraSetup
    {
      CameraId  = dto.CameraId,
      ServerId  = dto.ServerId ?? string.Empty,
      Cred      = dto.Cred == null ? null : new Credentials { User = dto.Cred.User, Pass = dto.Cred.Pass },
      OnvifConn = dto.OnvifConn == null ? null : new OnvifConnection { Ip = dto.OnvifConn.ip, Port = dto.OnvifConn.port },
    };

    if (dto.Streams.Count > 0)
    {
      setup.Streams.AddRange(dto.Streams.Select(s =>
      {
        var stream = new CameraStream
        {
          StreamName   = s.StreamName,
          ProfileToken = s.ProfileToken,
          RtspUrl      = s.RtspUrl,
          WebRtc       = s.WebRtc,
        };
        if (s.RecordingProfile != null)
          stream.RecordingProfile = ToProfileProto(s.RecordingProfile);
        return stream;
      }));
    }
    else if (!string.IsNullOrEmpty(dto.RtspUrl))
    {
      setup.Streams.Add(new CameraStream
      {
        StreamName = "main",
        RtspUrl    = dto.RtspUrl,
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
