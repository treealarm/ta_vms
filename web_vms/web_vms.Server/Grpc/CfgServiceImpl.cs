using Grpc.Core;
using System.Collections.Concurrent;
using Vms.Cfg;

namespace web_vms
{
  public class CfgServiceImpl(ICameraManager _manager) : CfgService.CfgServiceBase
  {

    private static CameraStateDto ToDto(CameraState s) => new()
    {
      CameraId = s.CameraId,
      Recording = s.Recording,
      WebRtc = s.WebRtc,
      RtspUrl = s.RtspUrl,
      Cred = s.Cred == null ? null : new
      CredentialsDto()
      {
        User = s.Cred.User,
        Pass = s.Cred.Pass
      }
    };

    private static CameraState ToProto(CameraStateDto d) => new()
    {
      CameraId = d.CameraId,
      Recording = d.Recording,
      WebRtc = d.WebRtc,
      RtspUrl = d.RtspUrl ?? "",
      Cred = d.Cred == null ? null : new Credentials()
      {
        User = d.Cred.User,
        Pass = d.Cred.Pass
      }

    };


    public override async Task<OperationStatus> AddCamera(CameraState request, ServerCallContext context)
    {
      var result = await _manager.Add(ToDto(request));

      return new OperationStatus { Success = result };
    }

    public override async Task<OperationStatus> RemoveCamera(SourceRequest request, ServerCallContext context)
    {
      var removed = await _manager.Remove(request.SourceId);

      return new OperationStatus
      {
        Success = removed,
        Message = removed ? "Removed" : "Not found"
      };
    }

    public override async Task<ListCamerasResponse> ListCameras(ListCamerasRequest request, ServerCallContext context)
    {
      var response = new ListCamerasResponse();

      var list = await _manager.List();
      response.Cameras.AddRange(list.Select(i=> ToProto(i)));

      return response;
    }

    public override async Task<OperationStatus> SetCameraState(CameraState request, ServerCallContext context)
    {
      var result = await _manager.SetState(ToDto(request));

      return new OperationStatus
      {
        Success = result
      };
    }

    public override async Task<CameraState> GetCameraState(SourceRequest request, ServerCallContext context)
    {
      var state = await _manager.Get(request.SourceId);


      return state != null ? ToProto(state!) : new CameraState();
    }
  }
}
