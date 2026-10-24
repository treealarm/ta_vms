using System.Collections.Concurrent;
using Vms.Cfg;
using Vms.Fs;

namespace VmsFs;

public sealed class CameraStorageCache(
  StorageService.StorageServiceClient storageClient,
  StorageListCache storageListCache,
  ILogger<CameraStorageCache> logger)
{
  private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(1);

  private record CacheEntry(IReadOnlyList<StorageInfo> Values, DateTime FetchedAt);

  private readonly ConcurrentDictionary<string, CacheEntry> _cache = new();
  private readonly ConcurrentDictionary<string, byte> _refreshing = new();

  // Bumped by every invalidation, under _gate together with the drop. A fetch stores its answer
  // only if the version is still the one it started under: one that was in flight when an event
  // arrived may hold the assignments from before the change, and storing it after the event would
  // undo the invalidation for a whole TTL. One counter for all cameras — an unrelated camera's
  // answer being thrown away costs one more round trip, and invalidations are rare.
  private readonly Lock _gate = new();
  private long _version;

  /// <summary>
  /// Whether this camera could record right now, and if not, why. Deliberately not cached and
  /// not derived from the cache above: the cache has already dropped disabled storages, so it
  /// cannot tell "nothing assigned" from "everything assigned is out of service" -- and those
  /// are different problems with different fixes. The probe write is the whole point of asking
  /// the file service rather than reading configuration.
  /// </summary>
  public async Task<RecordingReadiness> CheckReadinessAsync(string cameraId, CancellationToken ct)
  {
    GetCameraStoragesResponse assignments;
    try
    {
      assignments = await storageClient.GetCameraStoragesAsync(
        new CameraStoragesRequest { CameraId = cameraId }, cancellationToken: ct);
    }
    catch (Exception ex)
    {
      return new RecordingReadiness
      {
        Ready = false, Reason = "config_unavailable", Detail = ex.Message,
      };
    }

    if (assignments.Assignments.Count == 0)
      return new RecordingReadiness { Ready = false, Reason = "no_storage_assigned" };

    var storageList = await storageListCache.GetAsync(ct);
    var enabled = assignments.Assignments
      .OrderBy(a => a.Priority)
      .Select(a => storageListCache.Find(storageList, a.StorageId))
      .Where(s => s is not null && !s.Disabled)
      .ToList();

    if (enabled.Count == 0)
      return new RecordingReadiness { Ready = false, Reason = "all_storages_disabled" };

    // Any one writable storage is enough: recording walks them in priority order and takes the
    // first that accepts the segment.
    foreach (var storage in enabled)
    {
      if (WritableDirectoryBrowser.CanWrite(storage!.RootPath))
        return new RecordingReadiness { Ready = true };
    }

    var first = enabled[0]!;
    return new RecordingReadiness
    {
      Ready = false,
      Reason = "storage_not_writable",
      StorageId = first.StorageId,
      Detail = first.RootPath,
    };
  }

  // Returns all storages in priority order. First entry is the primary storage.
  // Throws when VmsCfg cannot be asked and nothing is cached: "could not read the configuration" is
  // a different problem from "nothing is assigned", and is not cached as one.
  public async Task<IReadOnlyList<StorageInfo>> GetAllAsync(string cameraId, CancellationToken ct)
  {
    if (_cache.TryGetValue(cameraId, out var entry))
    {
      if (DateTime.UtcNow - entry.FetchedAt >= Ttl)
        _ = RefreshInBackgroundAsync(cameraId);
      return entry.Values;
    }

    var version = Volatile.Read(ref _version);
    var storages = await FetchAsync(cameraId, ct);
    Store(cameraId, storages, version);
    return storages;
  }

  // Returns the primary (highest-priority) storage, or null if none assigned.
  public async Task<StorageInfo?> GetAsync(string cameraId, CancellationToken ct)
  {
    var all = await GetAllAsync(cameraId, ct);
    return all.Count > 0 ? all[0] : null;
  }

  public void Invalidate(string cameraId)
  {
    lock (_gate)
    {
      _version++;
      _cache.TryRemove(cameraId, out _);
    }
  }

  /// <summary>
  /// For a change to a storage itself rather than to one camera's assignments — going out of
  /// service, or being removed. Which cameras that touches is not in the event, and working it
  /// out would cost a round trip per camera to answer a question a clear of the map answers for
  /// free.
  /// </summary>
  public void InvalidateAll()
  {
    lock (_gate)
    {
      _version++;
      _cache.Clear();
    }
  }

  private void Store(string cameraId, IReadOnlyList<StorageInfo> storages, long version)
  {
    lock (_gate)
    {
      if (_version == version)
        _cache[cameraId] = new CacheEntry(storages, DateTime.UtcNow);
    }
  }

  // A failed refresh leaves the entry as it was: the assignments last read are a better answer than
  // none, and the next call past the TTL tries again.
  private async Task RefreshInBackgroundAsync(string cameraId)
  {
    if (!_refreshing.TryAdd(cameraId, 0))
      return;
    try
    {
      var version = Volatile.Read(ref _version);
      var storages = await FetchAsync(cameraId, CancellationToken.None);
      Store(cameraId, storages, version);
    }
    catch (Exception ex)
    {
      logger.LogWarning("Storage refresh for camera '{CameraId}' failed, keeping the cached list: {Error}",
        cameraId, ex.Message);
    }
    finally
    {
      _refreshing.TryRemove(cameraId, out _);
    }
  }

  private async Task<IReadOnlyList<StorageInfo>> FetchAsync(string cameraId, CancellationToken ct)
  {
    var assignmentsResp = await storageClient.GetCameraStoragesAsync(
      new CameraStoragesRequest { CameraId = cameraId }, cancellationToken: ct);

    var storageList = await storageListCache.GetAsync(ct);
    var result = new List<StorageInfo>();

    foreach (var assignment in assignmentsResp.Assignments.OrderBy(a => a.Priority))
    {
      var storage = storageListCache.Find(storageList, assignment.StorageId);
      if (storage is null)
        continue;

      // A disabled storage is out of service, not broken. Dropping it here -- before anything
      // tries to write to it -- is what keeps "on maintenance" distinguishable from "the disk
      // failed": otherwise video_srv walks it like any other candidate and logs the same
      // "not accessible" it logs for a genuine fault.
      //
      // Only the write path goes through here. Clips already on a disabled storage stay
      // readable, because playback and cleanup resolve their root from the storage list itself.
      if (storage.Disabled)
        continue;

      result.Add(new StorageInfo { StorageId = storage.StorageId, RootPath = storage.RootPath });
    }

    return result;
  }
}
