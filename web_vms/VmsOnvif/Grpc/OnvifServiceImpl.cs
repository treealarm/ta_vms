using Dapr.Actors;
using Dapr.Actors.Client;
using Grpc.Core;
using Vms.Cfg;
using Vms.Onvif;
using web_vms.Types.Actors;

namespace VmsOnvif;

public class OnvifServiceImpl(
  CfgService.CfgServiceClient cfgClient,
  OnvifEventSubscriptionService subscriptionService) : OnvifService.OnvifServiceBase
{
  private readonly CfgService.CfgServiceClient _cfgClient = cfgClient;

  public override async Task<GetProfilesResponse> GetProfiles(CameraRequest request, ServerCallContext context)
  {
    var state = await _cfgClient.GetCameraAsync(
      new SourceRequest { SourceId = request.SourceId },
      cancellationToken: context.CancellationToken);

    var response = new GetProfilesResponse();

    if (!state.Found || state.Setup?.OnvifConn == null)
      return response;

    var actor = CameraActorProxy(request.SourceId);
    List<ProfileInfoDto> profiles;
    try
    {
      profiles = await actor.InvokeMethodAsync<CameraConnInfo, List<ProfileInfoDto>>(
        "GetProfilesAsync", ToConn(state.Setup), context.CancellationToken);
    }
    catch (Exception ex)
    {
      throw new RpcException(new Status(StatusCode.Internal, $"Actor invoke failed: {ex.Message}"));
    }

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
    var state = await _cfgClient.GetCameraAsync(
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

    var actor = CameraActorProxy(request.SourceId);
    GetStreamUriActorResult result;
    try
    {
      result = await actor.InvokeMethodAsync<GetStreamUriActorRequest, GetStreamUriActorResult>(
        "GetStreamUriAsync", new GetStreamUriActorRequest(ToConn(state.Setup), request.ProfileToken),
        context.CancellationToken);
    }
    catch (Exception ex)
    {
      throw new RpcException(new Status(StatusCode.Internal, $"Actor invoke failed: {ex.Message}"));
    }

    return new StreamUriResponse
    {
      RtspUrl = string.IsNullOrEmpty(result.Uri) ? fallbackUrl : result.Uri
    };
  }

  public override Task<GetRecentOnvifEventsResponse> GetRecentOnvifEvents(
    GetRecentOnvifEventsRequest request, ServerCallContext context)
  {
    var events = subscriptionService.GetRecentEvents(request.CameraId);
    var response = new GetRecentOnvifEventsResponse();
    response.Events.AddRange(events.Select(e => new OnvifEventRecord
    {
      ReceivedAt = e.ReceivedAt.ToString("O"),
      Topic      = e.Topic,
      PayloadXml = e.PayloadXml,
    }));
    return Task.FromResult(response);
  }

  // Non-remoting proxy: uses JSON serialization, no DataContractSerializer
  private static ActorProxy CameraActorProxy(string cameraId) =>
    ActorProxy.Create(new ActorId(cameraId), "CameraActor");

  private static CameraConnInfo ToConn(CameraSetup setup) => new(
    setup.OnvifConn!.Ip,
    setup.OnvifConn.Port,
    setup.Cred?.User ?? string.Empty,
    setup.Cred?.Pass ?? string.Empty);
}
