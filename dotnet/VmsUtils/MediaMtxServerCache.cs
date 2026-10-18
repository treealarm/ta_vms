using System.Collections.Concurrent;
using Vms.Cfg;

namespace VmsUtils;

/// Which MediaMTX server serves a camera.
///
/// Resolved from VmsCfg once and then held. The assignment only changes when a camera or a MediaMTX
/// server row is edited, and both publish a config change that MediaMtxConfigSubscriber turns into
/// an invalidation here. Without the cache every reader-count poll — one per published stream, every
/// few seconds, from every video_srv — would put a GetOrAssign call on VmsCfg, which is exactly
/// the per-request config read in a hot path that CLAUDE.md forbids.
public sealed class MediaMtxServerCache(CfgService.CfgServiceClient cfgClient)
{
  // A null value means VmsCfg has no server for this camera. Cached like any other answer, so a
  // camera that cannot be assigned does not turn into a retry on every poll.
  private readonly ConcurrentDictionary<string, MediaMtxServer?> _cache = new(StringComparer.Ordinal);

  public async Task<MediaMtxServer?> GetAsync(string cameraId, CancellationToken ct)
  {
    if (_cache.TryGetValue(cameraId, out var cached))
      return cached;

    // GetOrAssign rather than a plain read: a camera that has never been polled has no server yet,
    // and assigning one is what makes it publishable. Only reachable on a miss.
    var reply = await cfgClient.GetOrAssignMediaMtxServerAsync(
      new SourceRequest { SourceId = cameraId }, cancellationToken: ct);

    var server = reply.Found && !string.IsNullOrWhiteSpace(reply.Server?.Host) ? reply.Server : null;
    _cache[cameraId] = server;
    return server;
  }

  public void Invalidate(string cameraId) => _cache.TryRemove(cameraId, out _);

  public void Clear() => _cache.Clear();
}
