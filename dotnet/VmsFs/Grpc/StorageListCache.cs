using Vms.Cfg;

namespace VmsFs;

public sealed class StorageListCache(
  StorageService.StorageServiceClient storageClient,
  ILogger<StorageListCache> logger)
{
  private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(1);

  private volatile IReadOnlyList<Storage>? _cached;
  private DateTime _fetchedAt = DateTime.MinValue;
  private int _refreshing;

  // As in CameraStorageCache: a fetch that was in flight when Invalidate ran stores nothing, or it
  // would put the list from before the change back for a whole TTL.
  private readonly Lock _gate = new();
  private long _version;

  public async Task<IReadOnlyList<Storage>> GetAsync(CancellationToken ct)
  {
    var cached = _cached;
    if (cached != null)
    {
      if (DateTime.UtcNow - _fetchedAt >= Ttl)
        _ = RefreshInBackgroundAsync();
      return cached;
    }

    return await FetchAndStoreAsync(ct);
  }

  public Storage? Find(IReadOnlyList<Storage> storages, string storageId) =>
    storages.FirstOrDefault(s => s.StorageId == storageId);

  public string? TryGetRootPath(string storageId) =>
    _cached?.FirstOrDefault(s => s.StorageId == storageId)?.RootPath;

  public void Invalidate()
  {
    lock (_gate)
    {
      _version++;
      _cached = null;
      _fetchedAt = DateTime.MinValue;
    }
  }

  private async Task RefreshInBackgroundAsync()
  {
    if (Interlocked.CompareExchange(ref _refreshing, 1, 0) != 0)
      return;
    try
    {
      await FetchAndStoreAsync(CancellationToken.None);
    }
    catch (Exception ex)
    {
      logger.LogWarning("Storage list refresh failed, keeping the cached list: {Error}", ex.Message);
    }
    finally
    {
      Interlocked.Exchange(ref _refreshing, 0);
    }
  }

  private async Task<IReadOnlyList<Storage>> FetchAndStoreAsync(CancellationToken ct)
  {
    var version = Volatile.Read(ref _version);
    var resp = await storageClient.ListStoragesAsync(new ListStoragesRequest(), cancellationToken: ct);
    var list = (IReadOnlyList<Storage>)resp.Storages.ToList();
    lock (_gate)
    {
      if (_version == version)
      {
        _cached = list;
        _fetchedAt = DateTime.UtcNow;
      }
    }
    return list;
  }
}
