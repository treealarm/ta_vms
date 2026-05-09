using Dapr.Client;
using Grpc.Core;
using Vms.Cfg;

namespace VmsCfg;

public class StorageServiceImpl(StorageDb db, DaprClient daprClient) : StorageService.StorageServiceBase
{
  private const string CameraStorageChangedTopic = "camera-storage-changed";
  private static readonly string PubSubName =
    Environment.GetEnvironmentVariable("PUBSUB_NAME")
      ?? throw new InvalidOperationException("Required environment variable 'PUBSUB_NAME' is not set");

  private void PublishStorageChanged(string cameraId) =>
    _ = Task.Run(async () =>
    {
      try { await daprClient.PublishEventAsync(PubSubName, CameraStorageChangedTopic, cameraId); }
      catch (Exception ex) { Console.Error.WriteLine($"camera-storage-changed publish failed: {ex.Message}"); }
    });
  public override async Task<OperationStatus> AddStorage(Storage request, ServerCallContext context)
  {
    if (string.IsNullOrWhiteSpace(request.StorageId))
      return new OperationStatus { Success = false, Message = "storage_id must not be empty" };
    if (string.IsNullOrWhiteSpace(request.RootPath))
      return new OperationStatus { Success = false, Message = "root_path must not be empty" };

    await db.AddStorageAsync(request, context.CancellationToken);
    return new OperationStatus { Success = true };
  }

  public override async Task<OperationStatus> RemoveStorage(StorageRequest request, ServerCallContext context)
  {
    var removed = await db.RemoveStorageAsync(request.StorageId, context.CancellationToken);
    return new OperationStatus { Success = removed, Message = removed ? "" : "Storage not found" };
  }

  public override async Task<ListStoragesResponse> ListStorages(
    ListStoragesRequest request, ServerCallContext context)
  {
    var response = new ListStoragesResponse();
    response.Storages.AddRange(await db.ListStoragesAsync(context.CancellationToken));
    return response;
  }

  public override async Task<OperationStatus> SetCameraStorages(
    SetCameraStoragesRequest request, ServerCallContext context)
  {
    if (string.IsNullOrWhiteSpace(request.CameraId))
      return new OperationStatus { Success = false, Message = "camera_id must not be empty" };

    await db.SetCameraStoragesAsync(request.CameraId, request.Assignments, context.CancellationToken);
    PublishStorageChanged(request.CameraId);
    return new OperationStatus { Success = true };
  }

  public override async Task<GetCameraStoragesResponse> GetCameraStorages(
    CameraStoragesRequest request, ServerCallContext context)
  {
    var assignments = await db.GetCameraStoragesAsync(request.CameraId, context.CancellationToken);
    var response = new GetCameraStoragesResponse();
    response.Assignments.AddRange(assignments);
    return response;
  }

}
