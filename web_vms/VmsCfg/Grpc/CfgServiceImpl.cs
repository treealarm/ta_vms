using System.Linq;
using Grpc.Core;
using Vms.Cfg;
using web_vms.Types.Dto;

namespace VmsCfg
{
  public class CfgServiceImpl : CfgService.CfgServiceBase
  {
    private readonly CameraStorage _storage;

    public CfgServiceImpl(CameraStorage storage)
    {
      _storage = storage;
    }

    private static CameraStateDto ToDto(CameraState s) => new()
    {
      CameraId = s.CameraId,
      Recording = s.Recording,
      WebRtc = s.WebRtc,
      RtspUrl = s.RtspUrl,
      Cred = s.Cred == null ? null : new CredentialsDto
      {
        User = s.Cred.User,
        Pass = s.Cred.Pass
      },
      OnvifConn = s.OnvifConn == null ? null : new OnvifConnectionDto
      {
        ip = s.OnvifConn.Ip,
        port = s.OnvifConn.Port
      }
    };

    private static CameraState ToProto(CameraStateDto d) => new()
    {
      CameraId = d.CameraId,
      Recording = d.Recording,
      WebRtc = d.WebRtc,
      RtspUrl = d.RtspUrl ?? string.Empty,
      Cred = d.Cred == null ? null : new Credentials
      {
        User = d.Cred.User,
        Pass = d.Cred.Pass
      },
      OnvifConn = d.OnvifConn == null ? null : new OnvifConnection
      {
        Ip = d.OnvifConn.ip,
        Port = d.OnvifConn.port
      }
    };

    public override Task<OperationStatus> AddCamera(CameraState request, ServerCallContext context)
    {
      var cameras = _storage.Load();
      cameras.RemoveAll(c => c.CameraId == request.CameraId);
      cameras.Add(ToDto(request));
      _storage.Save(cameras);

      return Task.FromResult(new OperationStatus { Success = true });
    }

    public override Task<OperationStatus> SetCameraState(CameraState request, ServerCallContext context)
    {
      var cameras = _storage.Load();
      var existing = cameras.FirstOrDefault(c => c.CameraId == request.CameraId);
      if (existing == null)
      {
        return Task.FromResult(new OperationStatus
        {
          Success = false,
          Message = "Camera not found"
        });
      }

      cameras.Remove(existing);
      cameras.Add(ToDto(request));
      _storage.Save(cameras);

      return Task.FromResult(new OperationStatus { Success = true });
    }

    public override Task<ListCamerasResponse> ListCameras(ListCamerasRequest request, ServerCallContext context)
    {
      var response = new ListCamerasResponse();
      var cameras = _storage.Load();
      response.Cameras.AddRange(cameras.Select(ToProto));
      return Task.FromResult(response);
    }

    public override Task<OperationStatus> RemoveCamera(SourceRequest request, ServerCallContext context)
    {
      var cameras = _storage.Load();
      var removed = cameras.RemoveAll(c => c.CameraId == request.SourceId) > 0;
      _storage.Save(cameras);

      return Task.FromResult(new OperationStatus { Success = removed });
    }

    public override Task<CameraStateResponse> GetCameraState(SourceRequest request, ServerCallContext context)
    {
      var state = _storage.Load().FirstOrDefault(c => c.CameraId == request.SourceId);
      if (state == null)
      {
        return Task.FromResult(new CameraStateResponse { Found = false });
      }

      return Task.FromResult(new CameraStateResponse
      {
        Found = true,
        Camera = ToProto(state)
      });
    }

    public override async Task<OnvifState> GetCameraOnvifUri(SourceRequest request, ServerCallContext context)
    {
      var response = new OnvifState();
      var state = _storage.Load().FirstOrDefault(c => c.CameraId == request.SourceId);

      if (state != null 
      && state.OnvifConn != null 
      && !string.IsNullOrEmpty(state.OnvifConn.ip) 
      && state.OnvifConn.port > 0)
      {
        var newUrl = await OnvifUtils.GetStreamUri(state);
        if (!string.IsNullOrEmpty(newUrl))
        {
          response.RtspUrl = newUrl;
        }
      }

      return response;
    }
  }
}
