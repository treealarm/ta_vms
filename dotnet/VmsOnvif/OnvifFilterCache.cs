using System.Collections.Concurrent;
using Grpc.Core;
using Vms.Cfg;

namespace VmsOnvif;

public sealed class OnvifFilterCache(CfgService.CfgServiceClient cfgClient)
{
  private static readonly TimeSpan CfgTimeout = TimeSpan.FromSeconds(5);
  private readonly ConcurrentDictionary<string, List<OnvifAlarmFilter>> _cache = new();

  public async Task<List<OnvifAlarmFilter>> GetAsync(string cameraId, CancellationToken ct)
  {
    if (_cache.TryGetValue(cameraId, out var cached))
      return cached;

    var opts = new CallOptions(deadline: DateTime.UtcNow.Add(CfgTimeout), cancellationToken: ct);
    try
    {
      var resp = await cfgClient.ListOnvifAlarmFiltersAsync(
        new ListOnvifAlarmFiltersRequest { CameraId = cameraId }, opts);
      var filters = resp.Filters.ToList();
      _cache[cameraId] = filters;
      return filters;
    }
    catch (Exception ex)
    {
      Console.Error.WriteLine(
        $"OnvifFilterCache: failed to fetch filters for '{cameraId}': {ex.Message}");
      return [];
    }
  }

  public void Invalidate(string cameraId) => _cache.TryRemove(cameraId, out _);

  public void InvalidateAll() => _cache.Clear();
}
