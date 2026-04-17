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
      var response = await _cfgClient.AddCameraAsync(ToProto(dto));
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

    public async Task<bool> SetState(CameraStateDto dto)
    {
      var response = await _cfgClient.SetCameraStateAsync(ToProto(dto));
      return response.Success;
    }

    public async Task<CameraStateDto?> Get(string id)
    {
      var response = await _cfgClient.GetCameraStateAsync(new SourceRequest { SourceId = id });
      return response.Found ? ToDto(response.Camera) : null;
    }

    private static CameraStateDto ToDto(CameraState state) => new()
    {
      CameraId = state.CameraId,
      Recording = state.Recording,
      WebRtc = state.WebRtc,
      RtspUrl = state.RtspUrl,
      Cred = state.Cred == null ? null : new CredentialsDto
      {
        User = state.Cred.User,
        Pass = state.Cred.Pass
      },
      OnvifConn = state.OnvifConn == null ? null : new OnvifConnectionDto
      {
        ip = state.OnvifConn.Ip,
        port = state.OnvifConn.Port
      }
    };

    private static CameraState ToProto(CameraStateDto dto) => new()
    {
      CameraId = dto.CameraId,
      Recording = dto.Recording,
      WebRtc = dto.WebRtc,
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
      }
    };
  }
}
