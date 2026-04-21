using Grpc.Core;
using Vms.Cfg;
using Vms.Onvif;

namespace VmsOnvif;

public class OnvifServiceImpl(CfgService.CfgServiceClient cfgClient) : OnvifService.OnvifServiceBase
{
  private readonly CfgService.CfgServiceClient _cfgClient = cfgClient;

  public override async Task<OnvifState> GetCameraOnvifUri(SourceRequest request, ServerCallContext context)
  {
    var response = new OnvifState();
    var state = await _cfgClient.GetCameraStateAsync(request, cancellationToken: context.CancellationToken);

    if (!state.Found
      || state.Camera?.OnvifConn == null
      || string.IsNullOrEmpty(state.Camera.OnvifConn.Ip)
      || state.Camera.OnvifConn.Port <= 0)
    {
      return response;
    }

    var newUrl = await OnvifUtils.GetStreamUri(state.Camera);
    if (!string.IsNullOrEmpty(newUrl))
    {
      response.RtspUrl = newUrl;
    }

    return response;
  }
}
