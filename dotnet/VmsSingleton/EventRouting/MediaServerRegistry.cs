using System.Collections.Concurrent;
using Vms.Cfg;

namespace VmsSingleton.EventRouting;

/// <summary>Which services a node runs that an action can address.</summary>
public sealed record NodeServices(string ServerId, string FsId, string OnvifId);

/// <summary>
/// Camera and server to the node's own services, both cached briefly.
/// </summary>
/// <remarks>
/// A <c>camera_id</c> alone does not name a service to call: retention lives in the node's VmsFs
/// and a relay in its VmsOnvif, and only VmsCfg's registry knows which node a camera belongs to.
/// Both lookups are admin-scale data that changes rarely, so they are cached for half a minute;
/// a camera moved between nodes is followed by that TTL, and an action arrives seconds after an
/// event, not milliseconds.
/// </remarks>
public sealed class MediaServerRegistry(CfgService.CfgServiceClient cfg)
{
  private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);
  private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(10);
  private static readonly TimeSpan CameraCallTimeout = TimeSpan.FromSeconds(5);

  private readonly ConcurrentDictionary<string, (string ServerId, DateTime FetchedAt)> _cameraServers =
    new(StringComparer.Ordinal);
  private readonly SemaphoreSlim _gate = new(1, 1);
  private Dictionary<string, NodeServices> _servers = new(StringComparer.Ordinal);
  private DateTime _serversFetchedAt = DateTime.MinValue;

  /// <summary>The node a camera belongs to, or null when VmsCfg does not know the camera.</summary>
  public async Task<NodeServices?> ForCameraAsync(string cameraId, CancellationToken ct)
  {
    if (!_cameraServers.TryGetValue(cameraId, out var entry)
        || DateTime.UtcNow - entry.FetchedAt >= CacheTtl)
    {
      var response = await cfg.GetCameraAsync(
        new SourceRequest { SourceId = cameraId },
        deadline: DateTime.UtcNow.Add(CameraCallTimeout),
        cancellationToken: ct);

      if (!response.Found || response.Setup is null)
      {
        _cameraServers.TryRemove(cameraId, out _);
        return null;
      }

      entry = (response.Setup.ServerId, DateTime.UtcNow);
      _cameraServers[cameraId] = entry;
    }

    return await ForServerAsync(entry.ServerId, ct);
  }

  public async Task<NodeServices?> ForServerAsync(string serverId, CancellationToken ct)
  {
    await RefreshAsync(ct);
    return _servers.GetValueOrDefault(serverId);
  }

  private async Task RefreshAsync(CancellationToken ct)
  {
    await _gate.WaitAsync(ct);
    try
    {
      if (DateTime.UtcNow - _serversFetchedAt < CacheTtl)
        return;

      var response = await cfg.ListMediaServersAsync(
        new ListMediaServersRequest(),
        deadline: DateTime.UtcNow.Add(CallTimeout),
        cancellationToken: ct);

      _servers = response.Servers
        .Where(x => !string.IsNullOrWhiteSpace(x.ServerId))
        .GroupBy(x => x.ServerId, StringComparer.Ordinal)
        .ToDictionary(
          g => g.Key,
          g => new NodeServices(g.Key, g.First().FsId, g.First().OnvifId),
          StringComparer.Ordinal);
      _serversFetchedAt = DateTime.UtcNow;
    }
    finally
    {
      _gate.Release();
    }
  }
}
