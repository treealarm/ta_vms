using System.Collections.Concurrent;
using Vms.Cfg;

namespace VmsAnalyticsSingleton;

/// <summary>
/// Which media server (and therefore which VmsFs) owns a camera. TTL cache plus explicit
/// invalidation from the server-config-changed subscription in <see cref="AnalyticsWatchSubscriber"/>.
/// </summary>
/// <remarks>
/// Exists because both callers sit on paths that are far too hot for a per-item gRPC round trip to
/// VmsCfg (CLAUDE.md forbids reading rarely-changing config in a hot path): metadata samples arrive
/// at several hertz per camera, and RetainEvent fires once per persisted detection. This is the
/// same TTL-plus-invalidation shape as <see cref="CameraStorageResolver"/>, including its rule that
/// a failed fetch is not an answer — a stale server id keeps working through a VmsCfg hiccup, while
/// caching "unknown" would silently stop archiving geometry until the TTL rolled over.
/// </remarks>
public sealed class CameraServerCache(
  CfgService.CfgServiceClient cfg,
  ILogger<CameraServerCache> logger)
{
  private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(1);

  private record Entry(string? ServerId, DateTime FetchedAt);

  private readonly ConcurrentDictionary<string, Entry> _entries = new();

  /// <returns>The owning server id, or null when the camera is unknown or VmsCfg never answered.</returns>
  public async Task<string?> GetServerIdAsync(string cameraId, CancellationToken ct)
  {
    _entries.TryGetValue(cameraId, out var entry);
    if (entry is not null && DateTime.UtcNow - entry.FetchedAt < Ttl)
      return entry.ServerId;

    try
    {
      var resp = await cfg.GetCameraAsync(new SourceRequest { SourceId = cameraId }, cancellationToken: ct);
      var serverId = resp.Found ? resp.Setup.ServerId : null;
      _entries[cameraId] = new Entry(serverId, DateTime.UtcNow);
      return serverId;
    }
    catch (Exception ex)
    {
      logger.LogWarning(ex, "CameraServerCache: failed to resolve server for camera '{CameraId}'", cameraId);
      return entry?.ServerId;
    }
  }

  /// <summary>
  /// Drops the named cameras, or everything when the list is empty — an empty camera_ids on
  /// server-config-changed means "something node-wide moved" and asks for a full reconcile.
  /// </summary>
  public void Invalidate(IReadOnlyCollection<string> cameraIds)
  {
    if (cameraIds.Count == 0)
    {
      _entries.Clear();
      return;
    }
    foreach (var id in cameraIds)
      _entries.TryRemove(id, out _);
  }
}
