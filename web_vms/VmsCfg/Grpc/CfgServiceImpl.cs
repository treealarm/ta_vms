using Grpc.Core;
using Vms.Cfg;

namespace VmsCfg
{
  public class CfgServiceImpl : CfgService.CfgServiceBase
  {
    private readonly CameraStorage _storage;

    public CfgServiceImpl(CameraStorage storage)
    {
      _storage = storage;
    }

    public override async Task<OperationStatus> SetupCamera(CameraSetup request, ServerCallContext context)
    {
      if (string.IsNullOrWhiteSpace(request.CameraId))
      {
        return new OperationStatus
        {
          Success = false,
          Message = "camera_id must not be empty"
        };
      }

      if (string.IsNullOrWhiteSpace(request.ServerId))
      {
        return new OperationStatus
        {
          Success = false,
          Message = "server_id must not be empty"
        };
      }

      await _storage.SetupCameraAsync(request, context.CancellationToken);
      return new OperationStatus { Success = true };
    }

    public override async Task<OperationStatus> RegisterMediaServer(MediaServerNode request, ServerCallContext context)
    {
      if (string.IsNullOrWhiteSpace(request.ServerId))
      {
        return new OperationStatus
        {
          Success = false,
          Message = "server_id must not be empty"
        };
      }

      await _storage.RegisterMediaServerAsync(request, context.CancellationToken);
      return new OperationStatus { Success = true };
    }

    public override async Task<ListMediaServersResponse> ListMediaServers(
      ListMediaServersRequest request,
      ServerCallContext context)
    {
      var response = new ListMediaServersResponse();
      response.Servers.AddRange(await _storage.ListMediaServersAsync(context.CancellationToken));
      return response;
    }

    public override async Task<OperationStatus> SetCameraState(CameraState request, ServerCallContext context)
    {
      var updated = await _storage.SetCameraStateAsync(request, context.CancellationToken);
      if (!updated)
      {
        return new OperationStatus
        {
          Success = false,
          Message = "Camera not found"
        };
      }

      return new OperationStatus { Success = true };
    }

    public override async Task<ListCamerasResponse> ListCameras(ListCamerasRequest request, ServerCallContext context)
    {
      var response = new ListCamerasResponse();
      response.Cameras.AddRange(await _storage.ListCamerasAsync(request.ServerId, context.CancellationToken));
      return response;
    }

    public override async Task<OperationStatus> RemoveCamera(SourceRequest request, ServerCallContext context)
    {
      var removed = await _storage.RemoveCameraAsync(request.SourceId, context.CancellationToken);

      return new OperationStatus { Success = removed };
    }

    public override async Task<CameraStateResponse> GetCameraState(SourceRequest request, ServerCallContext context)
    {
      var (found, setup) = await _storage.GetCameraAsync(request.SourceId, context.CancellationToken);
      if (!found || setup is null)
      {
        return new CameraStateResponse { Found = false };
      }

      return new CameraStateResponse
      {
        Found = true,
        Setup = setup,
        State = setup.State
      };
    }

  }
}
