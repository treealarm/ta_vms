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

    if (!state.Found || state.Setup is null)
    {
      return response;
    }

    if (state.Setup.OnvifConn == null
      || string.IsNullOrEmpty(state.Setup.OnvifConn.Ip)
      || state.Setup.OnvifConn.Port <= 0)
    {
      response.RtspUrl = state.Setup.RtspUrl;
      return response;
    }

    var newUrl = await OnvifUtils.GetStreamUri(state.Setup);
    if (!string.IsNullOrEmpty(newUrl))
    {
      response.RtspUrl = newUrl;
    }
    else
    {
      response.RtspUrl = state.Setup.RtspUrl;
    }

    return response;
  }
}
