using System.Collections.Concurrent;
using Dapr.Client;
using Grpc.Core;
using Vms.Cfg;
using Vms.Onvif;

namespace VmsPtzTrackingSingleton;

/// <summary>
/// Where a camera's PTZ commands have to go, and which ONVIF profile they name.
/// </summary>
/// <remarks>
/// Two lookups that always travel together, so they are cached together. Which VmsOnvif can reach a
/// camera is decided by which media server owns it — ONVIF talks over the same local subnet RTSP
/// does, so a central instance has no route (see MediaServerNode.onvif_id in vms_cfg.proto). The
/// profile token comes from the same CameraSetup.
///
/// Cached because a tracking loop issues commands several times a second per camera and config
/// reads have no business on that path (CLAUDE.md). Invalidation is by pub/sub rather than by a
/// version key in the state store: this service is a singleton, so an event reaches the only
/// instance there is. The TTL stays as the backstop for a VmsCfg that cannot be read.
///
/// A failed fetch is not an answer — the previous entry keeps working through a VmsCfg hiccup,
/// the same rule CameraServerCache in VmsAnalyticsSingleton follows.
/// </remarks>
public sealed class CameraPtzRouter(
  CfgService.CfgServiceClient cfg,
  ILogger<CameraPtzRouter> logger)
{
  private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(1);

  /// <param name="ProfileToken">
  /// Empty is legitimate: PtzServiceImpl resolves the camera's first profile itself when it gets
  /// no token, which is the right answer for a camera whose streams were never given one.
  /// </param>
  public sealed record Route(string OnvifAppId, string ProfileToken, bool HasTelemetry);

  private sealed record Entry(Route? Route, DateTime FetchedAt);

  private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
  private readonly ConcurrentDictionary<string, CallInvoker> _invokers = new(StringComparer.Ordinal);

  // Node ids change far more rarely than camera rows, but they come from the same event, so one
  // lock and one timestamp cover both.
  private readonly SemaphoreSlim _nodesLock = new(1, 1);
  private Dictionary<string, string>? _serverToOnvif;
  private DateTime _nodesFetchedAt = DateTime.MinValue;

  public void Invalidate(IReadOnlyCollection<string> cameraIds)
  {
    // An empty list is the event's way of saying "something node-wide moved" — a media server row,
    // a pairing — so everything is dropped rather than nothing.
    if (cameraIds.Count == 0)
    {
      _entries.Clear();
      _nodesFetchedAt = DateTime.MinValue;
      return;
    }

    foreach (var cameraId in cameraIds)
      _entries.TryRemove(cameraId, out _);
  }

  /// <returns>Null when the camera is unknown, has no node, or VmsCfg never answered.</returns>
  public async Task<Route?> ResolveAsync(string cameraId, CancellationToken ct)
  {
    if (_entries.TryGetValue(cameraId, out var cached) && DateTime.UtcNow - cached.FetchedAt < Ttl)
      return cached.Route;

    try
    {
      var resp = await cfg.GetCameraAsync(new SourceRequest { SourceId = cameraId }, cancellationToken: ct);
      if (!resp.Found)
      {
        _entries[cameraId] = new Entry(null, DateTime.UtcNow);
        return null;
      }

      var onvifAppId = await OnvifAppIdAsync(resp.Setup.ServerId, ct);
      var route = onvifAppId is null
        ? null
        : new Route(
            onvifAppId,
            resp.Setup.Streams.Select(s => s.ProfileToken).FirstOrDefault(t => !string.IsNullOrEmpty(t)) ?? string.Empty,
            resp.Setup.HasTelemetry);

      _entries[cameraId] = new Entry(route, DateTime.UtcNow);
      return route;
    }
    catch (Exception ex)
    {
      logger.LogWarning(ex, "Could not resolve the PTZ route for camera '{Camera}'", cameraId);
      return cached?.Route;  // a stale route beats no route while VmsCfg is unreachable
    }
  }

  public PtzService.PtzServiceClient PtzClient(string onvifAppId) =>
    // One invoker per app id, reused: a tracking loop sends commands in bursts, and building a new
    // connection to daprd for each one is what ServerPairRegistry avoids for the joystick already.
    new(_invokers.GetOrAdd(onvifAppId, appId => DaprClient.CreateInvocationInvoker(appId)));

  private async Task<string?> OnvifAppIdAsync(string serverId, CancellationToken ct)
  {
    if (string.IsNullOrWhiteSpace(serverId))
      return null;

    var map = _serverToOnvif;
    if (map is null || DateTime.UtcNow - _nodesFetchedAt >= Ttl)
      map = await RefreshNodesAsync(ct);

    if (map is not null && map.TryGetValue(serverId, out var onvifId) && !string.IsNullOrWhiteSpace(onvifId))
      return onvifId;

    // A node that names no VmsOnvif cannot be reached over ONVIF at all — same shape as a node
    // naming no roi_id: the feature is simply not available there.
    return null;
  }

  private async Task<Dictionary<string, string>?> RefreshNodesAsync(CancellationToken ct)
  {
    await _nodesLock.WaitAsync(ct);
    try
    {
      if (_serverToOnvif is not null && DateTime.UtcNow - _nodesFetchedAt < Ttl)
        return _serverToOnvif;

      var resp = await cfg.ListMediaServersAsync(new ListMediaServersRequest(), cancellationToken: ct);
      _serverToOnvif = resp.Servers
        .Where(s => !string.IsNullOrWhiteSpace(s.OnvifId))
        .ToDictionary(s => s.ServerId, s => s.OnvifId, StringComparer.OrdinalIgnoreCase);
      _nodesFetchedAt = DateTime.UtcNow;
      return _serverToOnvif;
    }
    catch (Exception ex)
    {
      logger.LogWarning(ex, "Could not list media servers; keeping the previous node map");
      return _serverToOnvif;
    }
    finally
    {
      _nodesLock.Release();
    }
  }
}
