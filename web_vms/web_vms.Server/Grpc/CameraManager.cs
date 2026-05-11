using System.Linq;
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
    var response = await _cfgClient.SetupCameraAsync(ToSetupProto(NormalizeDto(dto)), CfgOptions(ct));
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
    var normalized = NormalizeDto(dto);

    // Get current camera to preserve its setup, then update all streams' state
    var current = await Get(normalized.CameraId, ct);
    if (current is null)
      return false;

    var updatedStreams = current.Streams
      .Select(s => s with { RecordingMode = normalized.RecordingMode, WebRtc = normalized.WebRtc })
      .ToList();

    var updated = current with
    {
      RecordingMode = normalized.RecordingMode,
      WebRtc = normalized.WebRtc,
      Streams = updatedStreams
    };

    var response = await _cfgClient.SetupCameraAsync(ToSetupProto(updated), CfgOptions(ct));
    return response.Success;
  }

  public async Task<CameraSetupDto?> Get(string id, CancellationToken ct = default)
  {
    var response = await _cfgClient.GetCameraAsync(new SourceRequest { SourceId = id }, CfgOptions(ct));
    return response.Found ? ToDto(response.Setup) : null;
  }

  private static CameraSetupDto NormalizeDto(CameraSetupDto dto)
  {
    var mode = string.IsNullOrWhiteSpace(dto.RecordingMode)
      ? (dto.Recording ? "continuous" : "off")
      : dto.RecordingMode.Trim().ToLowerInvariant();

    if (mode is not ("off" or "continuous" or "prealarm"))
      mode = dto.Recording ? "continuous" : "off";

    var recording = mode is "continuous" or "prealarm";
    return dto with { RecordingMode = mode, Recording = recording };
  }

  public static CameraSetupDto Normalize(CameraSetupDto dto) => NormalizeDto(dto);

  private static CameraSetupDto ToDto(CameraSetup setup)
  {
    var streams = setup.Streams
      .Select(s => new CameraStreamDto
      {
        StreamName    = s.StreamName,
        ProfileToken  = s.ProfileToken,
        RtspUrl       = s.RtspUrl,
        WebRtc        = s.WebRtc,
        RecordingMode = RecordingModeToString(s.RecordingMode)
      })
      .ToList();

    var firstStream = streams.FirstOrDefault();

    return new CameraSetupDto
    {
      CameraId      = setup.CameraId,
      ServerId      = setup.ServerId,
      RecordingMode = firstStream?.RecordingMode ?? "off",
      Recording     = firstStream?.RecordingMode is "continuous" or "prealarm",
      WebRtc        = firstStream?.WebRtc ?? false,
      RtspUrl       = firstStream?.RtspUrl,
      Streams       = streams,
      Cred          = setup.Cred == null ? null : new CredentialsDto { User = setup.Cred.User, Pass = setup.Cred.Pass },
      OnvifConn     = setup.OnvifConn == null ? null : new OnvifConnectionDto { ip = setup.OnvifConn.Ip, port = setup.OnvifConn.Port },
      PreAlarm      = setup.PreAlarm == null ? null : new PreAlarmRecordingSettingsDto
      {
        PreRecordSeconds  = setup.PreAlarm.PreRecordSeconds,
        PostRecordSeconds = setup.PreAlarm.PostRecordSeconds,
        SegmentSeconds    = setup.PreAlarm.SegmentSeconds
      }
    };
  }

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
      PreAlarm  = dto.PreAlarm == null ? null : new PreAlarmRecordingSettings
      {
        PreRecordSeconds  = dto.PreAlarm.PreRecordSeconds,
        PostRecordSeconds = dto.PreAlarm.PostRecordSeconds,
        SegmentSeconds    = dto.PreAlarm.SegmentSeconds
      }
    };

    if (dto.Streams.Count > 0)
    {
      setup.Streams.AddRange(dto.Streams.Select(s => new CameraStream
      {
        StreamName    = s.StreamName,
        ProfileToken  = s.ProfileToken,
        RtspUrl       = s.RtspUrl,
        WebRtc        = s.WebRtc,
        RecordingMode = ParseRecordingMode(s.RecordingMode)
      }));
    }
    else if (!string.IsNullOrEmpty(dto.RtspUrl))
    {
      setup.Streams.Add(new CameraStream
      {
        StreamName    = "main",
        RtspUrl       = dto.RtspUrl,
        WebRtc        = dto.WebRtc,
        RecordingMode = ParseRecordingMode(dto.RecordingMode)
      });
    }

    return setup;
  }

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
