using Dapr.Client;
using Grpc.Core;
using Vms.Cfg;

namespace VmsCfg;

public class StorageServiceImpl(StorageDb db, DaprClient daprClient, ILogger<StorageServiceImpl> logger) : StorageService.StorageServiceBase
{
  private const string CameraStorageChangedTopic = "camera-storage-changed";
  // A change to a storage itself: its path, its reservation, whether it takes writes at all, or
  // its removal. Separate from camera-storage-changed, which is about one camera's assignments.
  private const string StorageConfigChangedTopic = "storage-config-changed";
  private static readonly string PubSubName =
    Environment.GetEnvironmentVariable("PUBSUB_NAME")
      ?? throw new InvalidOperationException("Required environment variable 'PUBSUB_NAME' is not set");

  /// <summary>
  /// Announces that a storage changed. Without this, disabling one takes effect only when every
  /// cache downstream happens to expire — VmsFs's and media_server's TTLs run in series, so a
  /// storage taken out of service kept accepting recordings for up to two minutes.
  /// </summary>
  private void PublishStorageConfigChanged(string storageId) =>
    _ = Task.Run(async () =>
    {
      try { await daprClient.PublishEventAsync(PubSubName, StorageConfigChangedTopic, storageId); }
      catch (Exception ex) { logger.LogWarning(ex, "storage-config-changed publish failed for '{StorageId}'", storageId); }
    });

  private void PublishStorageChanged(string cameraId) =>
    _ = Task.Run(async () =>
    {
      try { await daprClient.PublishEventAsync(PubSubName, CameraStorageChangedTopic, cameraId); }
      catch (Exception ex) { logger.LogWarning(ex, "camera-storage-changed publish failed for '{CameraId}'", cameraId); }
    });
  public override async Task<OperationStatus> AddStorage(Storage request, ServerCallContext context)
  {
    if (string.IsNullOrWhiteSpace(request.StorageId))
      return new OperationStatus { Success = false, Message = "storage_id must not be empty" };
    if (string.IsNullOrWhiteSpace(request.RootPath))
      return new OperationStatus { Success = false, Message = "root_path must not be empty" };

    await db.AddStorageAsync(request, context.CancellationToken);
    logger.LogInformation(
      "Storage '{StorageId}' added (root={RootPath}, disabled={Disabled})",
      request.StorageId, request.RootPath, request.Disabled);
    PublishStorageConfigChanged(request.StorageId);
    return new OperationStatus { Success = true };
  }

  public override async Task<OperationStatus> RemoveStorage(StorageRequest request, ServerCallContext context)
  {
    var removed = await db.RemoveStorageAsync(request.StorageId, context.CancellationToken);
    if (removed)
    {
      logger.LogInformation("Storage '{StorageId}' removed", request.StorageId);
      PublishStorageConfigChanged(request.StorageId);
    }
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
    logger.LogInformation("Camera '{CameraId}' storage assignments updated ({Count} entries)", request.CameraId, request.Assignments.Count);
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
