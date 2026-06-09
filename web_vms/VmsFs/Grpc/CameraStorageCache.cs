using System.Collections.Concurrent;
using Vms.Cfg;
using Vms.Fs;

namespace VmsFs;

public sealed class CameraStorageCache(
  StorageService.StorageServiceClient storageClient,
  StorageListCache storageListCache)
{
  private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(1);

  private record CacheEntry(IReadOnlyList<StorageInfo> Values, DateTime FetchedAt);

  private readonly ConcurrentDictionary<string, CacheEntry> _cache = new();
  private readonly ConcurrentDictionary<string, byte> _refreshing = new();

  // Returns all storages in priority order. First entry is the primary storage.
  public async Task<IReadOnlyList<StorageInfo>> GetAllAsync(string cameraId, CancellationToken ct)
  {
    if (_cache.TryGetValue(cameraId, out var entry))
    {
      if (DateTime.UtcNow - entry.FetchedAt >= Ttl)
        _ = RefreshInBackgroundAsync(cameraId);
      return entry.Values;
    }

    var storages = await FetchAsync(cameraId, ct);
    _cache[cameraId] = new CacheEntry(storages, DateTime.UtcNow);
    return storages;
  }

  // Returns the primary (highest-priority) storage, or null if none assigned.
  public async Task<StorageInfo?> GetAsync(string cameraId, CancellationToken ct)
  {
    var all = await GetAllAsync(cameraId, ct);
    return all.Count > 0 ? all[0] : null;
  }

  public void Invalidate(string cameraId) => _cache.TryRemove(cameraId, out _);

  private async Task RefreshInBackgroundAsync(string cameraId)
  {
    if (!_refreshing.TryAdd(cameraId, 0))
      return;
    try
    {
      var storages = await FetchAsync(cameraId, CancellationToken.None);
      _cache[cameraId] = new CacheEntry(storages, DateTime.UtcNow);
    }
    catch (Exception ex)
    {
      Console.Error.WriteLine($"CameraStorageCache background refresh failed for '{cameraId}': {ex.Message}");
    }
    finally
    {
      _refreshing.TryRemove(cameraId, out _);
    }
  }

  private async Task<IReadOnlyList<StorageInfo>> FetchAsync(string cameraId, CancellationToken ct)
  {
    GetCameraStoragesResponse assignmentsResp;
    try
    {
      assignmentsResp = await storageClient.GetCameraStoragesAsync(
        new CameraStoragesRequest { CameraId = cameraId }, cancellationToken: ct);
    }
    catch
    {
      return [];
    }

    var storageList = await storageListCache.GetAsync(ct);
    var result = new List<StorageInfo>();

    foreach (var assignment in assignmentsResp.Assignments.OrderBy(a => a.Priority))
    {
      var storage = storageListCache.Find(storageList, assignment.StorageId);
      if (storage != null)
        result.Add(new StorageInfo { StorageId = storage.StorageId, RootPath = storage.RootPath });
    }

    return result;
  }
}
