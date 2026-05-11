using Grpc.Core;
using OnvifLib;
using Vms.Cfg;
using Vms.Onvif;

namespace VmsOnvif;

public class OnvifServiceImpl(CfgService.CfgServiceClient cfgClient) : OnvifService.OnvifServiceBase
{
  private readonly CfgService.CfgServiceClient _cfgClient = cfgClient;

  public override async Task<GetProfilesResponse> GetProfiles(CameraRequest request, ServerCallContext context)
  {
    var state = await _cfgClient.GetCameraStateAsync(
      new SourceRequest { SourceId = request.SourceId },
      cancellationToken: context.CancellationToken);

    var response = new GetProfilesResponse();

    if (!state.Found || state.Setup?.OnvifConn == null)
      return response;

    var profiles = await OnvifUtils.GetProfiles(state.Setup);
    response.Profiles.AddRange(profiles.Select(p => new OnvifProfile
    {
      Token = p.Token,
      Name = p.Name,
      Width = p.Width,
      Height = p.Height,
      Encoding = p.Encoding
    }));
    return response;
  }

  public override async Task<StreamUriResponse> GetStreamUri(StreamUriRequest request, ServerCallContext context)
  {
    var state = await _cfgClient.GetCameraStateAsync(
      new SourceRequest { SourceId = request.SourceId },
      cancellationToken: context.CancellationToken);

    if (!state.Found || state.Setup is null)
      return new StreamUriResponse();

    var fallbackUrl = state.Setup.Streams
      .FirstOrDefault(s => s.ProfileToken == request.ProfileToken)?.RtspUrl
      ?? state.Setup.Streams.FirstOrDefault()?.RtspUrl
      ?? string.Empty;

    if (state.Setup.OnvifConn == null
      || string.IsNullOrEmpty(state.Setup.OnvifConn.Ip)
      || state.Setup.OnvifConn.Port <= 0)
    {
      return new StreamUriResponse { RtspUrl = fallbackUrl };
    }

    var uri = await OnvifUtils.GetStreamUri(state.Setup, request.ProfileToken);
    return new StreamUriResponse
    {
      RtspUrl = string.IsNullOrEmpty(uri) ? fallbackUrl : uri
    };
  }
}
