using System.Linq;
using Vms.Cfg;
using web_vms.Types.Dto;

namespace web_vms
{
  public class CameraManager : ICameraManager
  {
    private readonly CfgService.CfgServiceClient _cfgClient;

    public CameraManager(CfgService.CfgServiceClient cfgClient)
    {
      _cfgClient = cfgClient;
    }

    public async Task<bool> Add(CameraStateDto dto)
    {
      var response = await _cfgClient.SetupCameraAsync(ToSetupProto(dto));
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
      var response = await _cfgClient.SetCameraStateAsync(ToStateProto(dto));
      return response.Success;
    }

    public async Task<CameraStateDto?> Get(string id)
    {
      var response = await _cfgClient.GetCameraStateAsync(new SourceRequest { SourceId = id });
      return response.Found ? ToDto(response.Setup) : null;
    }

    private static CameraStateDto ToDto(CameraSetup setup) => new()
    {
      CameraId = setup.CameraId,
      ServerId = setup.ServerId,
      Recording = setup.State?.Recording ?? false,
      WebRtc = setup.State?.WebRtc ?? false,
      RtspUrl = setup.RtspUrl,
      Cred = setup.Cred == null ? null : new CredentialsDto
      {
        User = setup.Cred.User,
        Pass = setup.Cred.Pass
      },
      OnvifConn = setup.OnvifConn == null ? null : new OnvifConnectionDto
      {
        ip = setup.OnvifConn.Ip,
        port = setup.OnvifConn.Port
      }
    };

    private static MediaServerNodeDto ToDto(MediaServerNode server) => new()
    {
      ServerId = server.ServerId,
      Status = server.Status
    };

    private static CameraSetup ToSetupProto(CameraStateDto dto) => new()
    {
      CameraId = dto.CameraId,
      ServerId = dto.ServerId ?? string.Empty,
      RtspUrl = dto.RtspUrl ?? string.Empty,
      Cred = dto.Cred == null ? null : new Credentials
      {
        User = dto.Cred.User,
        Pass = dto.Cred.Pass
      },
      OnvifConn = dto.OnvifConn == null ? null : new OnvifConnection
      {
        Ip = dto.OnvifConn.ip,
        Port = dto.OnvifConn.port
      },
      State = ToStateProto(dto)
    };

    private static CameraState ToStateProto(CameraStateDto dto) => new()
    {
      CameraId = dto.CameraId,
      Recording = dto.Recording,
      WebRtc = dto.WebRtc
    };
  }
}
