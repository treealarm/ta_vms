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

  public async Task<bool> Add(CameraStateDto dto, CancellationToken ct = default)
  {
    var response = await _cfgClient.SetupCameraAsync(ToSetupProto(NormalizeDto(dto)), CfgOptions(ct));
    return response.Success;
  }

  public async Task<bool> Remove(string id, CancellationToken ct = default)
  {
    var response = await _cfgClient.RemoveCameraAsync(new SourceRequest { SourceId = id }, CfgOptions(ct));
    return response.Success;
  }

  public async Task<List<CameraStateDto>> List(CancellationToken ct = default)
  {
    var response = await _cfgClient.ListCamerasAsync(new ListCamerasRequest(), CfgOptions(ct));
    return response.Cameras.Select(ToDto).ToList();
  }

  public async Task<List<MediaServerNodeDto>> ListServers(CancellationToken ct = default)
  {
    var response = await _cfgClient.ListMediaServersAsync(new ListMediaServersRequest(), CfgOptions(ct));
    return response.Servers.Select(ToDto).ToList();
  }

  public async Task<bool> SetState(CameraStateDto dto, CancellationToken ct = default)
  {
    var normalized = NormalizeDto(dto);
    var response = await _cfgClient.SetCameraStateAsync(ToStateProto(normalized), CfgOptions(ct));
    return response.Success;
  }

  public async Task<CameraStateDto?> Get(string id, CancellationToken ct = default)
  {
    var response = await _cfgClient.GetCameraStateAsync(new SourceRequest { SourceId = id }, CfgOptions(ct));
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

    var streams = setup.Streams
      .Select(s => new CameraStreamDto
      {
        StreamName = s.StreamName,
        ProfileToken = s.ProfileToken,
        RtspUrl = s.RtspUrl
      })
      .ToList();

    return new CameraStateDto
    {
      CameraId = setup.CameraId,
      ServerId = setup.ServerId,
      Recording = mode is "continuous" or "prealarm",
      RecordingMode = mode,
      WebRtc = setup.State?.WebRtc ?? false,
      RtspUrl = streams.FirstOrDefault()?.RtspUrl,
      Streams = streams,
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

    if (dto.Streams.Count > 0)
    {
      setup.Streams.AddRange(dto.Streams.Select(s => new CameraStream
      {
        StreamName = s.StreamName,
        ProfileToken = s.ProfileToken,
        RtspUrl = s.RtspUrl
      }));
    }
    else if (!string.IsNullOrEmpty(dto.RtspUrl))
    {
      setup.Streams.Add(new CameraStream { StreamName = "main", RtspUrl = dto.RtspUrl });
    }

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
