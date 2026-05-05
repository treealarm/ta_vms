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

    public override async Task<OperationStatus> SetCameraState(CameraState request, ServerCallContext context)
    {
      var updated = await storage.SetCameraStateAsync(request, context.CancellationToken);
      if (!updated)
        return new OperationStatus { Success = false, Message = "Camera not found" };

      InvalidateFsCache(request.CameraId);
      return new OperationStatus { Success = true };
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

    public override async Task<CameraStateResponse> GetCameraState(SourceRequest request, ServerCallContext context)
    {
      var (found, setup) = await storage.GetCameraAsync(request.SourceId, context.CancellationToken);
      if (!found || setup is null)
        return new CameraStateResponse { Found = false };

      return new CameraStateResponse { Found = true, Setup = setup, State = setup.State };
    }
  }
}
