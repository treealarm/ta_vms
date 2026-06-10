using Dapr.Actors;
using Dapr.Actors.Client;
using Grpc.Core;
using Vms.Onvif;
using web_vms.Types.Actors;

namespace VmsOnvif;

public class OnvifServiceImpl(
  CameraConnCache connCache,
  OnvifEventSubscriptionService subscriptionService) : OnvifService.OnvifServiceBase
{
  public override async Task<GetProfilesResponse> GetProfiles(CameraRequest request, ServerCallContext context)
  {
    var data = await connCache.GetAsync(request.SourceId, context.CancellationToken);
    var response = new GetProfilesResponse();

    if (data?.Conn == null)
      return response;

    var actor = CameraActorProxy(request.SourceId);
    List<ProfileInfoDto> profiles;
    try
    {
      profiles = await actor.InvokeMethodAsync<CameraConnInfo, List<ProfileInfoDto>>(
        "GetProfilesAsync", data.Conn, context.CancellationToken);
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
    var data = await connCache.GetAsync(request.SourceId, context.CancellationToken);

    if (data == null)
      return new StreamUriResponse();

    var fallbackUrl = data.Streams
      .FirstOrDefault(s => s.ProfileToken == request.ProfileToken)?.RtspUrl
      ?? data.Streams.FirstOrDefault()?.RtspUrl
      ?? string.Empty;

    if (data.Conn == null)
      return new StreamUriResponse { RtspUrl = fallbackUrl };

    var actor = CameraActorProxy(request.SourceId);
    GetStreamUriActorResult result;
    try
    {
      result = await actor.InvokeMethodAsync<GetStreamUriActorRequest, GetStreamUriActorResult>(
        "GetStreamUriAsync", new GetStreamUriActorRequest(data.Conn, request.ProfileToken),
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
      CameraId   = request.CameraId,
    }));
    return Task.FromResult(response);
  }

  public override Task<GetAllRecentOnvifEventsResponse> GetAllRecentOnvifEvents(
    GetAllRecentOnvifEventsRequest request, ServerCallContext context)
  {
    var events = subscriptionService.GetAllRecentEvents();
    var response = new GetAllRecentOnvifEventsResponse();
    response.Events.AddRange(events.Select(e => new OnvifEventRecord
    {
      ReceivedAt = e.Entry.ReceivedAt.ToString("O"),
      Topic      = e.Entry.Topic,
      PayloadXml = e.Entry.PayloadXml,
      CameraId   = e.CameraId,
    }));
    return Task.FromResult(response);
  }

  // Non-remoting proxy: uses JSON serialization, no DataContractSerializer
  private static ActorProxy CameraActorProxy(string cameraId) =>
    ActorProxy.Create(new ActorId(cameraId), "CameraActor");
}
