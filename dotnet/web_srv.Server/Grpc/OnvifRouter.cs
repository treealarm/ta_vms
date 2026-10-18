using System.Collections.Concurrent;
using Dapr.Client;
using Grpc.Core;
using Vms.Onvif;

namespace web_srv;

/// Picks the VmsOnvif instance that can actually reach a given camera.
///
/// VmsOnvif is per node because ONVIF talks to a camera over the same local subnet RTSP does, so
/// "which instance" is decided by which media server owns the camera. Everything here is that
/// lookup, cached: PTZ arrives at joystick rates, and resolving the owner through VmsCfg on every
/// call would put a per-request config read back into a hot path.
///
/// Staleness matters — a camera moved to another node would keep receiving PTZ on the old one — so
/// the cache is dropped when VmsCfg bumps the shared cam-node-version key. web_srv.Server runs in
/// several replicas, which rules out a pub/sub event (a Dapr topic delivers to exactly one of them);
/// a key everybody reads has no such asymmetry. The TTL stays as the backstop for a state store
/// that cannot be read.
public sealed class OnvifRouter(ICameraManager cameras, ServerPairRegistry registry, DaprClient dapr)
{
  private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(60);
  private static readonly TimeSpan VersionProbeInterval = TimeSpan.FromSeconds(5);
  private static readonly TimeSpan VersionProbeTimeout = TimeSpan.FromSeconds(2);
  private const string VersionKey = "cam-node-version";
  // Best-effort: this only lights a button, so a slow node must not hold up the whole answer.
  private static readonly TimeSpan BboxProbeTimeout = TimeSpan.FromSeconds(3);

  private static readonly string StateStoreName =
    Environment.GetEnvironmentVariable("DAPR_STATE_STORE_NAME")
      ?? throw new InvalidOperationException("Required environment variable 'DAPR_STATE_STORE_NAME' is not set");

  private readonly ConcurrentDictionary<string, (string ServerId, DateTime ResolvedAt)> _cache =
    new(StringComparer.Ordinal);

  private readonly SemaphoreSlim _versionLock = new(1, 1);
  private DateTime _versionCheckedAtUtc = DateTime.MinValue;
  private string? _lastSeenVersion;

  public async Task<OnvifService.OnvifServiceClient> OnvifAsync(string cameraId, CancellationToken ct) =>
    registry.GetOnvifClient(await ServerIdAsync(cameraId, ct));

  /// Which node owns this camera. For callers that need to group several cameras into one call per
  /// node — the client objects above are created per call, so they cannot be grouped on directly.
  public Task<string> ResolveServerIdAsync(string cameraId, CancellationToken ct) =>
    ServerIdAsync(cameraId, ct);

  /// Which of these cameras are currently configured to stream their own object boxes, batched one
  /// call per node. A camera absent from the result has not been probed yet (or its node is down) --
  /// callers must not read that as "no", which is why the map is sparse rather than defaulted.
  ///
  /// Grouped by node id rather than by client object: the clients above are created per call, so
  /// grouping on those would silently degrade back to one request per camera.
  public async Task<Dictionary<string, bool>> GetBboxCapableAsync(
    IEnumerable<string> cameraIds, CancellationToken ct)
  {
    var groups = new Dictionary<string, List<string>>(StringComparer.Ordinal);
    foreach (var cameraId in cameraIds.Distinct())
    {
      string serverId;
      try
      {
        serverId = await ServerIdAsync(cameraId, ct);
      }
      catch (InvalidOperationException)
      {
        continue;  // camera with no media server assigned -- no node to ask
      }

      if (!groups.TryGetValue(serverId, out var list))
        groups[serverId] = list = [];
      list.Add(cameraId);
    }

    var result = new Dictionary<string, bool>(StringComparer.Ordinal);
    foreach (var (serverId, ids) in groups)
    {
      try
      {
        var request = new MetadataStatesRequest();
        request.CameraIds.AddRange(ids);
        var resp = await registry.GetOnvifClient(serverId).GetMetadataStatesAsync(
          request, new CallOptions(deadline: DateTime.UtcNow.Add(BboxProbeTimeout), cancellationToken: ct));
        foreach (var pair in resp.BboxCapable)
          result[pair.Key] = pair.Value;
      }
      catch (RpcException)
      {
        // A node that is down contributes nothing: the button stays dark, which is the right
        // answer for cameras nobody can reach anyway.
      }
    }
    return result;
  }

  public async Task<PtzService.PtzServiceClient> PtzAsync(string cameraId, CancellationToken ct) =>
    registry.GetPtzClient(await ServerIdAsync(cameraId, ct));

  public async Task<DeviceIoService.DeviceIoServiceClient> DeviceIoAsync(string cameraId, CancellationToken ct) =>
    registry.GetDeviceIoClient(await ServerIdAsync(cameraId, ct));

  private async Task<string> ServerIdAsync(string cameraId, CancellationToken ct)
  {
    if (string.IsNullOrWhiteSpace(cameraId))
      throw new InvalidOperationException("cameraId must not be empty for ONVIF routing");

    await EnsureFreshAsync(ct);

    if (_cache.TryGetValue(cameraId, out var cached) && DateTime.UtcNow - cached.ResolvedAt < Ttl)
      return cached.ServerId;

    var camera = await cameras.Get(cameraId, ct);
    if (camera is null || string.IsNullOrWhiteSpace(camera.ServerId))
      throw new InvalidOperationException($"Camera '{cameraId}' is unknown or has no media server assigned");

    _cache[cameraId] = (camera.ServerId, DateTime.UtcNow);
    return camera.ServerId;
  }

  /// Drops the cache when VmsCfg has rewritten the shared version since the last probe. Never
  /// throws: an unreadable state store leaves the cache in place, degrading propagation to the TTL
  /// rather than failing every camera call.
  private async Task EnsureFreshAsync(CancellationToken ct)
  {
    if (DateTime.UtcNow - _versionCheckedAtUtc < VersionProbeInterval)
      return;

    // Best-effort, and never a queue: every PTZ, snapshot and bbox-capability call routes through
    // here, so waiting on someone else's state store read would serialize all of them behind it.
    if (!await _versionLock.WaitAsync(0, ct))
      return;

    try
    {
      if (DateTime.UtcNow - _versionCheckedAtUtc < VersionProbeInterval)
        return;

      string version;
      try
      {
        // Bounded on its own, because the lock above is held for the duration of this call.
        using var probe = CancellationTokenSource.CreateLinkedTokenSource(ct);
        probe.CancelAfter(VersionProbeTimeout);

        version = await dapr.GetStateAsync<string>(StateStoreName, VersionKey, cancellationToken: probe.Token)
          ?? string.Empty;
      }
      catch
      {
        // Leave _versionCheckedAtUtc alone so the next call retries rather than waiting the interval.
        return;
      }

      _versionCheckedAtUtc = DateTime.UtcNow;

      // The first read only records where the counter stands: the cache is empty anyway, and
      // flushing on it would discard every replica's warm cache on restart for nothing.
      if (_lastSeenVersion is null)
      {
        _lastSeenVersion = version;
        return;
      }

      if (string.Equals(_lastSeenVersion, version, StringComparison.Ordinal))
        return;

      _lastSeenVersion = version;
      _cache.Clear();
    }
    finally
    {
      _versionLock.Release();
    }
  }
}
