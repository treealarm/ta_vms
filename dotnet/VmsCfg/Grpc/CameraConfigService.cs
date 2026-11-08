using Microsoft.Extensions.Logging;
using Vms.Cfg;

namespace VmsCfg;

/// <summary>
/// Camera configuration writes that announce themselves the way an administrator's save does: the
/// camera itself with its storage default, and the PTZ binding and policy that aim one camera with
/// another.
/// </summary>
/// <remarks>
/// A service of its own rather than methods of <see cref="CfgServiceImpl"/> because the demo
/// installer writes through it, and the gRPC service depends on the installer (its demo button
/// calls it) — so the installer cannot depend on the service back.
/// </remarks>
public sealed class CameraConfigService(
  CameraStorage storage,
  NodeRegistry nodes,
  StorageDb storageDb,
  ConfigNotifier notifier,
  ILogger<CameraConfigService> logger)
{
  /// <summary>
  /// Writes a camera the way an administrator's save does — storage, default storage, the cache
  /// invalidation and the node-version bump — for a caller with no call context: the demo
  /// installer provisioning the demo's cameras.
  /// </summary>
  public async Task<OperationStatus> ConfigureCameraAsync(CameraSetup request, CancellationToken ct)
  {
    if (!Guid.TryParse(request.CameraId, out _))
      return new OperationStatus { Success = false, Message = "camera_id must be a UUID" };

    if (string.IsNullOrWhiteSpace(request.ServerId))
      return new OperationStatus { Success = false, Message = "server_id must not be empty" };

    if (!await nodes.IsKnownServerAsync(request.ServerId, ct))
      return new OperationStatus { Success = false, Message = $"server_id '{request.ServerId}' is not a known media server" };

    // Get old server before updating so we can notify it if server_id changed
    var (_, existing) = await storage.GetCameraAsync(request.CameraId, ct);

    await storage.SetupCameraAsync(request, ct);
    logger.LogInformation("Camera '{CameraId}' configured (server={ServerId})", request.CameraId, request.ServerId);
    await EnsureCameraHasStorageAsync(request.CameraId, ct);
    // The camera's own event already names this node — InvalidateFsCache resolves it from the
    // row just written — so there is no second, node-wide notification here. That one would make
    // every subscriber drop its whole cache on an ordinary camera edit.
    notifier.InvalidateFsCache(request.CameraId);

    // The node it left hears about the camera by name, so it can release it at once instead of
    // waiting for its next reconciliation sweep.
    if (existing is not null && existing.ServerId != request.ServerId)
      notifier.NotifyCameraChanged(existing.ServerId, request.CameraId);

    // A new camera has no cached route yet; a moved one has a stale one.
    if (existing is null || existing.ServerId != request.ServerId)
      await notifier.BumpCamNodeVersionAsync(ct);

    return new OperationStatus { Success = true };
  }


  /// <summary>
  /// Gives a camera with no storage assignment the first configured storage.
  /// </summary>
  /// <remarks>
  /// A camera without one silently records nothing and cannot receive an archive import — the
  /// failure only surfaces much later as "no writable storage", long after whoever added the
  /// camera has moved on. An explicit assignment is still respected: this only fills a gap, and
  /// only when a storage exists to fill it with.
  /// </remarks>
  private async Task EnsureCameraHasStorageAsync(string cameraId, CancellationToken ct)
  {
    try
    {
      var assigned = await storageDb.GetCameraStoragesAsync(cameraId, ct);
      if (assigned.Count > 0)
        return;

      var storages = await storageDb.ListStoragesAsync(ct);
      if (storages.Count == 0)
      {
        logger.LogWarning(
          "Camera '{CameraId}' has no storage assigned and none is configured — it will record nothing",
          cameraId);
        return;
      }

      var fallback = storages[0];
      await storageDb.SetCameraStoragesAsync(
        cameraId,
        [new CameraStorageAssignment { StorageId = fallback.StorageId, Priority = 0 }],
        ct);
      logger.LogInformation(
        "Camera '{CameraId}' had no storage assigned; defaulted it to '{StorageId}'",
        cameraId, fallback.StorageId);
    }
    catch (Exception ex)
    {
      // Never fail camera setup over this — the camera is still usable, just without a place to
      // record until someone assigns one.
      logger.LogWarning(ex, "Could not assign a default storage to camera '{CameraId}'", cameraId);
    }
  }


/// <summary>The policy write, for the demo installer: it has no call context.</summary>
  /// <summary>The body of the policy write, for the demo installer: it has no call context.</summary>
  public async Task<OperationStatus> ConfigurePtzTrackingAsync(PtzTrackingPolicy? policy, CancellationToken ct)
  {
    if (string.IsNullOrWhiteSpace(policy?.CameraId))
      return new OperationStatus { Success = false, Message = "camera_id must not be empty" };

    var result = await storage.UpsertPtzTrackingPolicyAsync(policy, ct);
    logger.LogInformation(
      "Self-guidance for camera '{CameraId}' is now {State}",
      result.CameraId, result.Enabled ? "on" : "off");
    // The same signal every other camera write sends. VmsPtzTrackingSingleton is subscribed to it
    // and reconciles its sessions against the new configuration -- which is what makes switching
    // the mode on start a camera tracking without anything else being pressed.
    notifier.InvalidateFsCache(result.CameraId);
    return new OperationStatus { Success = true };
  }


/// <summary>The binding write, for the demo installer: it has no call context.</summary>
  /// <summary>The body of the binding write, for the demo installer: it has no call context.</summary>
  public async Task<OperationStatus> ConfigurePtzSlaveBindingAsync(PtzSlaveBinding? binding, CancellationToken ct)
  {
    if (string.IsNullOrWhiteSpace(binding?.SlaveCameraId))
      return new OperationStatus { Success = false, Message = "slave_camera_id must not be empty" };
    if (string.IsNullOrWhiteSpace(binding.MasterCameraId))
      return new OperationStatus { Success = false, Message = "master_camera_id must not be empty" };
    if (binding.MasterCameraId == binding.SlaveCameraId)
      return new OperationStatus
      {
        Success = false,
        // Not merely useless: it would have the open-loop aiming law fight the closed-loop one
        // over the same motor, each moving the picture the other is measuring.
        Message = "A camera cannot be its own master -- switch self-guidance on instead",
      };

    var result = await storage.UpsertPtzSlaveBindingAsync(binding, ct);
    logger.LogInformation(
      "Slave binding for camera '{Slave}' -> master '{Master}' upserted: {Points} point(s), method {Method}",
      result.SlaveCameraId, result.MasterCameraId, result.Points.Count, result.Method);
    notifier.InvalidateFsCache(result.SlaveCameraId);
    return new OperationStatus { Success = true };
  }


/// <summary>Takes a slave's binding away, for the demo's removal.</summary>
public async Task<OperationStatus> RemovePtzSlaveBindingAsync(string slaveCameraId, CancellationToken ct)
{
  if (string.IsNullOrWhiteSpace(slaveCameraId))
    return new OperationStatus { Success = false, Message = "slave_camera_id must not be empty" };

  var removed = await storage.RemovePtzSlaveBindingAsync(slaveCameraId, ct);
  if (removed)
  {
    logger.LogInformation("Slave binding for camera '{Slave}' removed", slaveCameraId);
    notifier.InvalidateFsCache(slaveCameraId);
  }
  return new OperationStatus { Success = removed };
}
}
