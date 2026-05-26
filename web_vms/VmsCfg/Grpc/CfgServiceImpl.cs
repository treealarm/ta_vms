using Dapr.Client;
using Grpc.Core;
using Vms.Cfg;

namespace VmsCfg
{
  public class CfgServiceImpl(CameraStorage storage, DaprClient daprClient) : CfgService.CfgServiceBase
  {
    private const string CameraConfigChangedTopic = "camera-config-changed";
    private readonly string _pubSubName =
      Environment.GetEnvironmentVariable("PUBSUB_NAME")
        ?? throw new InvalidOperationException("Required environment variable 'PUBSUB_NAME' is not set");

    private void InvalidateFsCache(string cameraId)
    {
      _ = Task.Run(async () =>
      {
        try
        {
          await daprClient.PublishEventAsync(_pubSubName, CameraConfigChangedTopic, cameraId);
        }
        catch (Exception ex)
        {
          Console.Error.WriteLine($"Camera config invalidation publish failed for '{cameraId}': {ex.Message}");
        }
      });
    }

    public override async Task<OperationStatus> SetupCamera(CameraSetup request, ServerCallContext context)
    {
      if (string.IsNullOrWhiteSpace(request.CameraId))
        return new OperationStatus { Success = false, Message = "camera_id must not be empty" };

      if (string.IsNullOrWhiteSpace(request.ServerId))
        return new OperationStatus { Success = false, Message = "server_id must not be empty" };

      await storage.SetupCameraAsync(request, context.CancellationToken);
      InvalidateFsCache(request.CameraId);
      return new OperationStatus { Success = true };
    }

    public override async Task<OperationStatus> RegisterMediaServer(MediaServerNode request, ServerCallContext context)
    {
      if (string.IsNullOrWhiteSpace(request.ServerId))
        return new OperationStatus { Success = false, Message = "server_id must not be empty" };

      await storage.RegisterMediaServerAsync(request, context.CancellationToken);
      return new OperationStatus { Success = true };
    }

    public override async Task<ListMediaServersResponse> ListMediaServers(
      ListMediaServersRequest request,
      ServerCallContext context)
    {
      var response = new ListMediaServersResponse();
      response.Servers.AddRange(await storage.ListMediaServersAsync(context.CancellationToken));
      return response;
    }

    public override async Task<ListCamerasResponse> ListCameras(ListCamerasRequest request, ServerCallContext context)
    {
      var response = new ListCamerasResponse();
      response.Cameras.AddRange(await storage.ListCamerasAsync(request.ServerId, context.CancellationToken));
      return response;
    }

    public override async Task<OperationStatus> RemoveCamera(SourceRequest request, ServerCallContext context)
    {
      var removed = await storage.RemoveCameraAsync(request.SourceId, context.CancellationToken);
      if (removed)
        InvalidateFsCache(request.SourceId);
      return new OperationStatus { Success = removed };
    }

    public override async Task<GetCameraResponse> GetCamera(SourceRequest request, ServerCallContext context)
    {
      var (found, setup) = await storage.GetCameraAsync(request.SourceId, context.CancellationToken);
      if (!found || setup is null)
        return new GetCameraResponse { Found = false };

      return new GetCameraResponse { Found = true, Setup = setup };
    }

    public override async Task<ListRecordingProfilesResponse> ListRecordingProfiles(
      ListRecordingProfilesRequest request,
      ServerCallContext context)
    {
      var response = new ListRecordingProfilesResponse();
      response.Profiles.AddRange(await storage.ListRecordingProfilesAsync(context.CancellationToken));
      return response;
    }

    public override async Task<OperationStatus> UpsertRecordingProfile(
      RecordingProfile request,
      ServerCallContext context)
    {
      if (string.IsNullOrWhiteSpace(request.Name))
        return new OperationStatus { Success = false, Message = "name must not be empty" };

      await storage.UpsertRecordingProfileAsync(request, context.CancellationToken);
      return new OperationStatus { Success = true };
    }

    public override async Task<OperationStatus> RemoveRecordingProfile(
      RecordingProfileRequest request,
      ServerCallContext context)
    {
      if (string.IsNullOrWhiteSpace(request.Id))
        return new OperationStatus { Success = false, Message = "id must not be empty" };

      var removed = await storage.RemoveRecordingProfileAsync(request.Id, context.CancellationToken);
      return new OperationStatus { Success = removed };
    }

    public override async Task<OperationStatus> SetStreamOverride(
      SetStreamOverrideRequest request,
      ServerCallContext context)
    {
      if (string.IsNullOrWhiteSpace(request.CameraId) || string.IsNullOrWhiteSpace(request.StreamName))
        return new OperationStatus { Success = false, Message = "camera_id and stream_name are required" };

      if (string.IsNullOrWhiteSpace(request.ProfileId) || !Guid.TryParse(request.ProfileId, out _))
        return new OperationStatus { Success = false, Message = "profile_id must be a valid UUID" };

      await storage.SetStreamOverrideAsync(request.CameraId, request.StreamName, request.ProfileId, context.CancellationToken);
      InvalidateFsCache(request.CameraId);
      return new OperationStatus { Success = true };
    }

    public override async Task<OperationStatus> ClearStreamOverride(
      ClearStreamOverrideRequest request,
      ServerCallContext context)
    {
      if (string.IsNullOrWhiteSpace(request.CameraId) || string.IsNullOrWhiteSpace(request.StreamName))
        return new OperationStatus { Success = false, Message = "camera_id and stream_name are required" };

      await storage.ClearStreamOverrideAsync(request.CameraId, request.StreamName, context.CancellationToken);
      InvalidateFsCache(request.CameraId);
      return new OperationStatus { Success = true };
    }

    public override async Task<ActivateAlarmOverrideResponse> ActivateAlarmOverride(
      ActivateAlarmOverrideRequest request,
      ServerCallContext context)
    {
      if (string.IsNullOrWhiteSpace(request.CameraId) || request.Until is null)
        return new ActivateAlarmOverrideResponse { Activated = false };

      var streamName = request.HasStreamName && !string.IsNullOrWhiteSpace(request.StreamName)
        ? request.StreamName : null;
      var until = request.Until.ToDateTime().ToUniversalTime();

      var activated = await storage.ActivateAlarmOverrideAsync(request.CameraId, streamName, until, context.CancellationToken);
      if (activated)
        InvalidateFsCache(request.CameraId);
      return new ActivateAlarmOverrideResponse { Activated = activated };
    }

    public override async Task<OperationStatus> ClearAlarmOverride(
      ClearAlarmOverrideRequest request,
      ServerCallContext context)
    {
      if (string.IsNullOrWhiteSpace(request.CameraId) || string.IsNullOrWhiteSpace(request.StreamName))
        return new OperationStatus { Success = false, Message = "camera_id and stream_name are required" };

      var cleared = await storage.ClearAlarmOverrideAsync(request.CameraId, request.StreamName, context.CancellationToken);
      if (cleared)
        InvalidateFsCache(request.CameraId);
      return new OperationStatus { Success = true };
    }

    public override async Task<ListAlarmOverrideStreamsResponse> ListAlarmOverrideStreams(
      ListAlarmOverrideStreamsRequest request,
      ServerCallContext context)
    {
      var streams = await storage.ListAlarmOverrideStreamsAsync(context.CancellationToken);
      var response = new ListAlarmOverrideStreamsResponse();
      response.Streams.AddRange(streams.Select(s => new AlarmOverrideStream
      {
        CameraId   = s.CameraId,
        StreamName = s.StreamName,
      }));
      return response;
    }

    public override async Task<ListMediaMtxServersResponse> ListMediaMtxServers(
      ListMediaMtxServersRequest request,
      ServerCallContext context)
    {
      var response = new ListMediaMtxServersResponse();
      response.Servers.AddRange(await storage.ListMediaMtxServersAsync(context.CancellationToken));
      return response;
    }

    public override async Task<OperationStatus> UpsertMediaMtxServer(
      MediaMtxServer request,
      ServerCallContext context)
    {
      if (string.IsNullOrWhiteSpace(request.Name))
        return new OperationStatus { Success = false, Message = "name must not be empty" };

      if (string.IsNullOrWhiteSpace(request.RtspUrl))
        return new OperationStatus { Success = false, Message = "rtsp_url must not be empty" };

      if (string.IsNullOrWhiteSpace(request.WebrtcUrl))
        return new OperationStatus { Success = false, Message = "webrtc_url must not be empty" };

      await storage.UpsertMediaMtxServerAsync(request, context.CancellationToken);
      return new OperationStatus { Success = true };
    }

    public override async Task<OperationStatus> RemoveMediaMtxServer(
      MediaMtxServerRequest request,
      ServerCallContext context)
    {
      if (string.IsNullOrWhiteSpace(request.Id))
        return new OperationStatus { Success = false, Message = "id must not be empty" };

      var removed = await storage.RemoveMediaMtxServerAsync(request.Id, context.CancellationToken);
      return new OperationStatus { Success = removed };
    }

    public override async Task<GetOrAssignMediaMtxServerResponse> GetOrAssignMediaMtxServer(
      SourceRequest request,
      ServerCallContext context)
    {
      if (string.IsNullOrWhiteSpace(request.SourceId))
        return new GetOrAssignMediaMtxServerResponse { Found = false };

      var server = await storage.GetOrAssignMediaMtxServerAsync(request.SourceId, context.CancellationToken);
      if (server is null)
        return new GetOrAssignMediaMtxServerResponse { Found = false };

      return new GetOrAssignMediaMtxServerResponse { Found = true, Server = server };
    }

    public override async Task<ListMonitorsResponse> ListMonitors(
      ListMonitorsRequest request,
      ServerCallContext context)
    {
      var response = new ListMonitorsResponse();
      response.Monitors.AddRange(await storage.ListMonitorsAsync(context.CancellationToken));
      return response;
    }

    public override async Task<MonitorSetup> UpsertMonitor(MonitorSetup request, ServerCallContext context)
    {
      if (string.IsNullOrWhiteSpace(request.Name))
        throw new RpcException(new Status(StatusCode.InvalidArgument, "name must not be empty"));

      if (string.IsNullOrWhiteSpace(request.Layout))
        throw new RpcException(new Status(StatusCode.InvalidArgument, "layout must not be empty"));

      return await storage.UpsertMonitorAsync(request, context.CancellationToken);
    }

    public override async Task<OperationStatus> RemoveMonitor(SourceRequest request, ServerCallContext context)
    {
      if (string.IsNullOrWhiteSpace(request.SourceId))
        return new OperationStatus { Success = false, Message = "source_id must not be empty" };

      var removed = await storage.RemoveMonitorAsync(request.SourceId, context.CancellationToken);
      return new OperationStatus { Success = removed };
    }

    public override async Task<OperationStatus> SetMonitorCameras(
      SetMonitorCamerasRequest request,
      ServerCallContext context)
    {
      if (string.IsNullOrWhiteSpace(request.MonitorId))
        return new OperationStatus { Success = false, Message = "monitor_id must not be empty" };

      await storage.SetMonitorCamerasAsync(request.MonitorId, request.CameraIds, context.CancellationToken);
      return new OperationStatus { Success = true };
    }

    public override async Task<OperationStatus> SetMonitorSlot(
      SetMonitorSlotRequest request,
      ServerCallContext context)
    {
      if (string.IsNullOrWhiteSpace(request.MonitorId) || string.IsNullOrWhiteSpace(request.CameraId))
        return new OperationStatus { Success = false, Message = "monitor_id and camera_id must not be empty" };

      await storage.SetMonitorSlotAsync(request.MonitorId, request.SlotIndex, request.CameraId, context.CancellationToken);
      return new OperationStatus { Success = true };
    }

    public override async Task<ListOnvifAlarmFiltersResponse> ListOnvifAlarmFilters(
      ListOnvifAlarmFiltersRequest request,
      ServerCallContext context)
    {
      if (string.IsNullOrWhiteSpace(request.CameraId))
        return new ListOnvifAlarmFiltersResponse();

      var filters = await storage.ListOnvifAlarmFiltersAsync(request.CameraId, context.CancellationToken);
      var response = new ListOnvifAlarmFiltersResponse();
      response.Filters.AddRange(filters);
      return response;
    }

    public override async Task<OnvifAlarmFilter> UpsertOnvifAlarmFilter(
      OnvifAlarmFilter request,
      ServerCallContext context)
    {
      if (string.IsNullOrWhiteSpace(request.CameraId))
        throw new RpcException(new Status(StatusCode.InvalidArgument, "camera_id is required"));
      if (string.IsNullOrWhiteSpace(request.FilterText))
        throw new RpcException(new Status(StatusCode.InvalidArgument, "filter_text is required"));
      if (request.TimeoutSeconds <= 0)
        request.TimeoutSeconds = 30;

      var result = await storage.UpsertOnvifAlarmFilterAsync(request, context.CancellationToken);
      InvalidateFsCache(result.CameraId);
      return result;
    }

    public override async Task<OperationStatus> RemoveOnvifAlarmFilter(
      OnvifAlarmFilterRequest request,
      ServerCallContext context)
    {
      if (string.IsNullOrWhiteSpace(request.Id))
        return new OperationStatus { Success = false, Message = "id is required" };

      var (removed, cameraId) = await storage.RemoveOnvifAlarmFilterAsync(request.Id, context.CancellationToken);
      if (removed && cameraId is not null)
        InvalidateFsCache(cameraId);
      return new OperationStatus { Success = removed };
    }
  }
}
