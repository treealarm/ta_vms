using Vms.Cfg;

namespace VmsFs;

public sealed class StorageListCache(StorageService.StorageServiceClient storageClient)
{
  private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(1);

  private volatile IReadOnlyList<Storage>? _cached;
  private DateTime _fetchedAt = DateTime.MinValue;
  private int _refreshing;

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
    _cached = null;
    _fetchedAt = DateTime.MinValue;
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
      Console.Error.WriteLine($"StorageListCache background refresh failed: {ex.Message}");
    }
    finally
    {
      Interlocked.Exchange(ref _refreshing, 0);
    }
  }

  private async Task<IReadOnlyList<Storage>> FetchAndStoreAsync(CancellationToken ct)
  {
    var resp = await storageClient.ListStoragesAsync(new ListStoragesRequest(), cancellationToken: ct);
    var list = (IReadOnlyList<Storage>)resp.Storages.ToList();
    _cached = list;
    _fetchedAt = DateTime.UtcNow;
    return list;
  }
}
