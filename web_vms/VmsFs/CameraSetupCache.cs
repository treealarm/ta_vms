using System.Collections.Concurrent;
using Vms.Cfg;

namespace VmsFs;

public sealed record CameraStreamSetup(
  string StreamName,
  string ProfileToken,
  string Path,
  bool WebRtc);

public sealed record CameraSetup(
  string ServerId,
  IReadOnlyList<CameraStreamSetup> Streams,
  Credentials? Cred);

public sealed class CameraSetupCache(CfgService.CfgServiceClient cfgClient)
{
  private readonly ConcurrentDictionary<string, CameraSetup?> _cache = new();

  public async Task<CameraSetup?> GetAsync(string cameraId, CancellationToken ct)
  {
    if (_cache.TryGetValue(cameraId, out var cached))
      return cached;

    GetCameraResponse resp;
    try
    {
      resp = await cfgClient.GetCameraAsync(
        new SourceRequest { SourceId = cameraId }, cancellationToken: ct);
    }
    catch
    {
      return null;
    }

    if (!resp.Found || resp.Setup is null || string.IsNullOrWhiteSpace(resp.Setup.ServerId))
    {
      _cache.TryAdd(cameraId, null);
      return null;
    }

    var setup = new CameraSetup(
      resp.Setup.ServerId,
      resp.Setup.Streams
        .Select(s => new CameraStreamSetup(s.StreamName, s.ProfileToken, s.Path, s.WebRtc))
        .ToList(),
      resp.Setup.Cred);

    _cache.TryAdd(cameraId, setup);
    return setup;
  }

  public void Invalidate(string cameraId) => _cache.TryRemove(cameraId, out _);

  /// For "everything on this node changed", where the event names no cameras.
  public void InvalidateAll() => _cache.Clear();
}
