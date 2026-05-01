using System.Linq;
using Vms.Cfg;
using web_vms.Types.Dto;

namespace web_vms;

public class CameraManager : ICameraManager
{
  private readonly CfgService.CfgServiceClient _cfgClient;

  public CameraManager(CfgService.CfgServiceClient cfgClient)
  {
    _cfgClient = cfgClient;
  }

  public async Task<bool> Add(CameraStateDto dto)
  {
    var response = await _cfgClient.SetupCameraAsync(ToSetupProto(NormalizeDto(dto)));
    return response.Success;
  }

  public async Task<bool> Remove(string id)
  {
    var response = await _cfgClient.RemoveCameraAsync(new SourceRequest { SourceId = id });
    return response.Success;
  }

  public async Task<List<CameraStateDto>> List()
  {
    var response = await _cfgClient.ListCamerasAsync(new ListCamerasRequest());
    return response.Cameras.Select(ToDto).ToList();
  }

  public async Task<List<MediaServerNodeDto>> ListServers()
  {
    var response = await _cfgClient.ListMediaServersAsync(new ListMediaServersRequest());
    return response.Servers.Select(ToDto).ToList();
  }

  public async Task<bool> SetState(CameraStateDto dto)
  {
    var normalized = NormalizeDto(dto);
    var response = await _cfgClient.SetCameraStateAsync(ToStateProto(normalized));
    return response.Success;
  }

  public async Task<CameraStateDto?> Get(string id)
  {
    var response = await _cfgClient.GetCameraStateAsync(new SourceRequest { SourceId = id });
    return response.Found ? ToDto(response.Setup) : null;
  }

  private static CameraStateDto NormalizeDto(CameraStateDto dto)
  {
    var mode = string.IsNullOrWhiteSpace(dto.RecordingMode)
      ? (dto.Recording ? "continuous" : "off")
      : dto.RecordingMode.Trim().ToLowerInvariant();

    if (mode is not ("off" or "continuous" or "prealarm"))
      mode = dto.Recording ? "continuous" : "off";

    var recording = mode is "continuous" or "prealarm";
    return dto with { RecordingMode = mode, Recording = recording };
  }

  public static CameraStateDto Normalize(CameraStateDto dto) => NormalizeDto(dto);

  private static CameraStateDto ToDto(CameraSetup setup)
  {
    var mode = setup.State?.RecordingMode switch
    {
      global::Vms.Cfg.RecordingMode.RecordingContinuous => "continuous",
      global::Vms.Cfg.RecordingMode.RecordingPrealarm => "prealarm",
      _ => "off",
    };

    return new CameraStateDto
    {
      CameraId = setup.CameraId,
      ServerId = setup.ServerId,
      Recording = mode is "continuous" or "prealarm",
      RecordingMode = mode,
      WebRtc = setup.State?.WebRtc ?? false,
      RtspUrl = setup.RtspUrl,
      Cred = setup.Cred == null
        ? null
        : new CredentialsDto
        {
          User = setup.Cred.User,
          Pass = setup.Cred.Pass
        },
      OnvifConn = setup.OnvifConn == null
        ? null
        : new OnvifConnectionDto
        {
          ip = setup.OnvifConn.Ip,
          port = setup.OnvifConn.Port
        },
      PreAlarm = setup.PreAlarm == null
        ? null
        : new PreAlarmRecordingSettingsDto
        {
          PreRecordSeconds = setup.PreAlarm.PreRecordSeconds,
          PostRecordSeconds = setup.PreAlarm.PostRecordSeconds,
          SegmentSeconds = setup.PreAlarm.SegmentSeconds
        }
    };
  }

  private static MediaServerNodeDto ToDto(MediaServerNode server) => new()
  {
    ServerId = server.ServerId,
    Status = server.Status
  };

  private static CameraSetup ToSetupProto(CameraStateDto dto)
  {
    var setup = new CameraSetup
    {
      CameraId = dto.CameraId,
      ServerId = dto.ServerId ?? string.Empty,
      RtspUrl = dto.RtspUrl ?? string.Empty,
      Cred = dto.Cred == null
        ? null
        : new Credentials
        {
          User = dto.Cred.User,
          Pass = dto.Cred.Pass
        },
      OnvifConn = dto.OnvifConn == null
        ? null
        : new OnvifConnection
        {
          Ip = dto.OnvifConn.ip,
          Port = dto.OnvifConn.port
        },
      State = ToStateProto(dto),
      PreAlarm = dto.PreAlarm == null
        ? null
        : new PreAlarmRecordingSettings
        {
          PreRecordSeconds = dto.PreAlarm.PreRecordSeconds,
          PostRecordSeconds = dto.PreAlarm.PostRecordSeconds,
          SegmentSeconds = dto.PreAlarm.SegmentSeconds
        }
    };

    return setup;
  }

  private static CameraState ToStateProto(CameraStateDto dto)
  {
    var normalized = NormalizeDto(dto);
    var modeStr = normalized.RecordingMode;
    return new CameraState
    {
      CameraId = normalized.CameraId,
      WebRtc = normalized.WebRtc,
      RecordingMode = modeStr switch
      {
        "continuous" => global::Vms.Cfg.RecordingMode.RecordingContinuous,
        "prealarm" => global::Vms.Cfg.RecordingMode.RecordingPrealarm,
        _ => global::Vms.Cfg.RecordingMode.RecordingOff,
      }
    };
  }
}
