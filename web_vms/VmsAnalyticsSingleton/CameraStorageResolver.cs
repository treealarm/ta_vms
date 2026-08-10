using System.Collections.Concurrent;
using Vms.Cfg;

namespace VmsAnalyticsSingleton;

public sealed record CameraStorage(string StorageId, string RootPath);

// Resolves a camera's primary storage volume (same priority model VmsFs uses for clips — see
// VmsFs.CameraStorageCache) so detection crops land on the camera's own storage, referenced by
// storage_id + relative path exactly like clips. TTL caches + explicit invalidation from the
// storage-config-changed subscription in AnalyticsWatchSubscriber.
public sealed class CameraStorageResolver(
  StorageService.StorageServiceClient storageClient,
  ILogger<CameraStorageResolver> logger)
{
  private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(1);

  private record AssignmentEntry(string? StorageId, DateTime FetchedAt);

  private readonly ConcurrentDictionary<string, AssignmentEntry> _assignments = new();
  private IReadOnlyDictionary<string, string> _storageRoots = new Dictionary<string, string>();
  private DateTime _storageRootsFetchedAt = DateTime.MinValue;

  // Returns the primary (highest-priority) storage of the camera, or null when none assigned —
  // in which case the crop is simply not saved (the detection row is still written).
  public async Task<CameraStorage?> GetPrimaryAsync(string cameraId, CancellationToken ct)
  {
    _assignments.TryGetValue(cameraId, out var entry);
    if (entry is null || DateTime.UtcNow - entry.FetchedAt >= Ttl)
    {
      var (ok, storageId) = await FetchPrimaryStorageIdAsync(cameraId, ct);
      if (ok)
      {
        entry = new AssignmentEntry(storageId, DateTime.UtcNow);
        _assignments[cameraId] = entry;
      }
      // A failed fetch is NOT "no storage assigned": don't cache it — fall back to the stale
      // entry if we have one (crops keep flowing through a transient VmsCfg hiccup) and retry
      // on the next detection.
    }
    if (entry?.StorageId is null)
      return null;

    var root = await GetRootPathAsync(entry.StorageId, ct);
    return root is null ? null : new CameraStorage(entry.StorageId, root);
  }

  public async Task<string?> GetRootPathAsync(string storageId, CancellationToken ct)
  {
    if (DateTime.UtcNow - _storageRootsFetchedAt >= Ttl)
    {
      try
      {
        var storages = await storageClient.ListStoragesAsync(new ListStoragesRequest(), cancellationToken: ct);
        _storageRoots = storages.Storages.ToDictionary(s => s.StorageId, s => s.RootPath);
        _storageRootsFetchedAt = DateTime.UtcNow;
      }
      catch (Exception ex)
      {
        logger.LogWarning(ex, "CameraStorageResolver: ListStorages failed — using last known storage list");
      }
    }
    return _storageRoots.TryGetValue(storageId, out var root) ? root : null;
  }

  public void InvalidateAll()
  {
    _assignments.Clear();
    _storageRootsFetchedAt = DateTime.MinValue;
  }

  private async Task<(bool Ok, string? StorageId)> FetchPrimaryStorageIdAsync(string cameraId, CancellationToken ct)
  {
    try
    {
      var assignments = await storageClient.GetCameraStoragesAsync(
        new CameraStoragesRequest { CameraId = cameraId }, cancellationToken: ct);
      return (true, assignments.Assignments.OrderBy(a => a.Priority).FirstOrDefault()?.StorageId);
    }
    catch (Exception ex)
    {
      logger.LogWarning(ex, "CameraStorageResolver: failed to resolve storage for camera '{CameraId}'", cameraId);
      return (false, null);
    }
  }
}
