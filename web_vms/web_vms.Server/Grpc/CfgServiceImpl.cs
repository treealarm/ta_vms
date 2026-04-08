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


    public override async Task<ListCamerasResponse> ListCameras(ListCamerasRequest request, ServerCallContext context)
    {
      var response = new ListCamerasResponse();

      var list = await _manager.List();
      response.Cameras.AddRange(list.Select(i=> ToProto(i)));

      return response;
    }

    public override async Task<OnvifState> GetCameraOnvifUri(SourceRequest request, ServerCallContext context)
    {
      var response = new OnvifState();

      var state = await _manager.Get(request.SourceId);

      if (state != null && state.OnvifConn != null
        &&
        !string.IsNullOrEmpty(state.OnvifConn.ip)
        &&
        state.OnvifConn.port > 0)
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
