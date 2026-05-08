using System.Collections.Concurrent;
using Vms.Cfg;
using Vms.Fs;

namespace VmsFs;

public sealed class CameraStorageCache(
  StorageService.StorageServiceClient storageClient,
  StorageListCache storageListCache)
{
  private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(1);

  private record CacheEntry(StorageInfo? Value, DateTime FetchedAt);

  private readonly ConcurrentDictionary<string, CacheEntry> _cache = new();
  private readonly ConcurrentDictionary<string, byte> _refreshing = new();

  public async Task<StorageInfo?> GetAsync(string cameraId, CancellationToken ct)
  {
    if (_cache.TryGetValue(cameraId, out var entry))
    {
      if (DateTime.UtcNow - entry.FetchedAt >= Ttl)
        _ = RefreshInBackgroundAsync(cameraId);
      return entry.Value;
    }

    var storage = await FetchAsync(cameraId, ct);
    _cache[cameraId] = new CacheEntry(storage, DateTime.UtcNow);
    return storage;
  }

  public void Invalidate(string cameraId) => _cache.TryRemove(cameraId, out _);

  private async Task RefreshInBackgroundAsync(string cameraId)
  {
    if (!_refreshing.TryAdd(cameraId, 0))
      return;
    try
    {
      var storage = await FetchAsync(cameraId, CancellationToken.None);
      _cache[cameraId] = new CacheEntry(storage, DateTime.UtcNow);
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

  private async Task<StorageInfo?> FetchAsync(string cameraId, CancellationToken ct)
  {
    GetCameraStoragesResponse assignmentsResp;
    try
    {
      assignmentsResp = await storageClient.GetCameraStoragesAsync(
        new CameraStoragesRequest { CameraId = cameraId }, cancellationToken: ct);
    }
    catch
    {
      return null;
    }

    var storages = await storageListCache.GetAsync(ct);

    foreach (var assignment in assignmentsResp.Assignments.OrderBy(a => a.Priority))
    {
      var storage = storageListCache.Find(storages, assignment.StorageId);
      if (storage != null)
        return new StorageInfo { StorageId = storage.StorageId, RootPath = storage.RootPath };
    }

    return null;
  }
}
