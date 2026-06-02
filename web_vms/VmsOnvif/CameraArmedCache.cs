using System.Collections.Concurrent;
using Grpc.Core;
using Vms.Cfg;

namespace VmsOnvif;

public sealed class CameraArmedCache(CfgService.CfgServiceClient cfgClient)
{
  private static readonly TimeSpan CfgTimeout = TimeSpan.FromSeconds(5);
  private readonly ConcurrentDictionary<string, bool> _cache = new();

  public async Task<bool> GetAsync(string cameraId, CancellationToken ct)
  {
    if (_cache.TryGetValue(cameraId, out var cached))
      return cached;

    var opts = new CallOptions(deadline: DateTime.UtcNow.Add(CfgTimeout), cancellationToken: ct);
    try
    {
      var resp = await cfgClient.GetCamStateAsync(
        new GetCamStateRequest { CameraId = cameraId }, opts);
      _cache[cameraId] = resp.Armed;
      return resp.Armed;
    }
    catch (Exception ex)
    {
      Console.Error.WriteLine(
        $"CameraArmedCache: failed to fetch armed state for '{cameraId}': {ex.Message}");
      return false;
    }
  }

  public void Invalidate(string cameraId) => _cache.TryRemove(cameraId, out _);
}
