using System.Collections.Concurrent;
using System.Security.Claims;
using Dapr.Client;
using Grpc.Core;
using web_srv.Authentication;
using MapProto = Vms.Map;

namespace web_srv.Authorization;

public interface IRightsService
{
  /// <summary>False = denied. Never throws on a permission miss — callers decide 403 vs 404.</summary>
  Task<bool> IsAllowedAsync(ClaimsPrincipal user, string cameraId, string action, CancellationToken ct);

  /// <summary>
  /// Which objects this action is permitted on. List endpoints filter with it; query-backed ones
  /// push it down as a filter so paging stays correct.
  /// </summary>
  Task<RightsScope> ScopeAsync(ClaimsPrincipal user, string action, CancellationToken ct);

  /// <summary>Drops one user's cached grants, or all of them when the username is empty.</summary>
  void Invalidate(string username);
}

/// <summary>
/// The gateway side of the one rights model. VmsMap owns the rows and the inheritance; this asks it
/// for one user's effective set and caches the answer, because this sits on every request —
/// including per-camera state polls and archive segment fetches — and CLAUDE.md forbids a config
/// round-trip on a hot path.
///
/// Freshness comes from the `rights-version` key VmsMap rewrites on every rights or mode write:
/// this service re-reads it at most once a second and drops the whole cache when it moved. That is
/// a shared signal rather than a per-instance one, so every replica converges within a second — a
/// pub/sub subscription could not, since all replicas share one Dapr consumer group and only one of
/// them would ever be told. The TTL below stays as the backstop for a state store hiccup.
/// </summary>
public sealed class RightsService : IRightsService
{
  private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);
  private static readonly TimeSpan VersionProbeInterval = TimeSpan.FromSeconds(1);
  private static readonly TimeSpan VersionProbeTimeout = TimeSpan.FromSeconds(2);
  private static readonly TimeSpan MapCallTimeout = TimeSpan.FromSeconds(5);
  private const string VersionKey = "rights-version";

  private readonly MapProto.MapService.MapServiceClient _map;
  private readonly DaprClient _dapr;
  private readonly ILogger<RightsService> _logger;

  private readonly string _stateStoreName =
    Environment.GetEnvironmentVariable("DAPR_STATE_STORE_NAME")
      ?? throw new InvalidOperationException("Required environment variable 'DAPR_STATE_STORE_NAME' is not set");

  private readonly SemaphoreSlim _versionLock = new(1, 1);
  private string? _lastSeenVersion;
  private DateTime _versionCheckedAtUtc = DateTime.MinValue;

  private int _generation;

  private sealed record CachedRights(MapProto.EffectiveRightsResponse Rights, DateTime FetchedAtUtc);

  private readonly ConcurrentDictionary<string, CachedRights> _byUser = new(StringComparer.Ordinal);
  private readonly ConcurrentDictionary<string, SemaphoreSlim> _refreshLocks = new(StringComparer.Ordinal);

  public RightsService(
    MapProto.MapService.MapServiceClient map,
    DaprClient dapr,
    ILogger<RightsService> logger)
  {
    _map = map ?? throw new ArgumentNullException(nameof(map));
    _dapr = dapr ?? throw new ArgumentNullException(nameof(dapr));
    _logger = logger ?? throw new ArgumentNullException(nameof(logger));
  }

  public async Task<bool> IsAllowedAsync(
    ClaimsPrincipal user, string cameraId, string action, CancellationToken ct)
  {
    if (string.IsNullOrWhiteSpace(cameraId))
      return false;

    var scope = await ScopeAsync(user, action, ct);
    return scope.Contains(cameraId);
  }

  public async Task<RightsScope> ScopeAsync(
    ClaimsPrincipal user, string action, CancellationToken ct)
  {
    if (!AuthGate.KeycloakEnabled)
      return RightsScope.Everything;

    if (user.IsInRole(RoleConstants.Admin))
      return RightsScope.Everything;

    await EnsureFreshAsync(ct);

    var username = user.Identity?.Name;
    if (string.IsNullOrEmpty(username))
      return RightsScope.Nothing;

    var rights = await GetRightsAsync(user, username, ct);
    if (rights is null)
      return RightsScope.Nothing;

    // Open mode is the old camera ACL's open setting: a node without rows is open to everyone.
    // The map's own reads still apply the finer rule for explicit rows; the legacy camera lists
    // keep the old blanket behaviour until M4 moves them onto the tree.
    if (rights.Mode == MapProto.RightsMode.Open)
      return RightsScope.Everything;

    var ids = rights.Nodes
      .Where(n => !string.IsNullOrEmpty(n.CameraId)
                  && n.Actions.Contains(action, StringComparer.Ordinal))
      .Select(n => n.CameraId)
      .ToHashSet(StringComparer.Ordinal);

    return RightsScope.Of(ids);
  }

  public void Invalidate(string username)
  {
    Interlocked.Increment(ref _generation);

    if (string.IsNullOrEmpty(username))
    {
      _byUser.Clear();
      _logger.LogInformation("Rights cache flushed");
      return;
    }

    _byUser.TryRemove(username, out _);
    _logger.LogInformation("Rights for '{Username}' invalidated", username);
  }

  /// <summary>
  /// Drops the cache when VmsMap has rewritten the shared rights version since the last probe.
  /// Never throws: a state store that cannot be read leaves the cache in place, so an infrastructure
  /// hiccup slows propagation down to <see cref="CacheTtl"/> instead of locking operators out.
  /// </summary>
  private async Task EnsureFreshAsync(CancellationToken ct)
  {
    if (DateTime.UtcNow - _versionCheckedAtUtc < VersionProbeInterval)
      return;

    if (!await _versionLock.WaitAsync(0, ct))
      return;

    try
    {
      if (DateTime.UtcNow - _versionCheckedAtUtc < VersionProbeInterval)
        return;

      using var probe = CancellationTokenSource.CreateLinkedTokenSource(ct);
      probe.CancelAfter(VersionProbeTimeout);

      var version = await _dapr.GetStateAsync<string>(
        _stateStoreName, VersionKey, cancellationToken: probe.Token) ?? string.Empty;
      _versionCheckedAtUtc = DateTime.UtcNow;

      if (_lastSeenVersion is null)
      {
        _lastSeenVersion = version;
        return;
      }

      if (string.Equals(_lastSeenVersion, version, StringComparison.Ordinal))
        return;

      _lastSeenVersion = version;
      Invalidate(string.Empty);
    }
    catch (OperationCanceledException) when (ct.IsCancellationRequested)
    {
      throw;
    }
    catch (Exception ex)
    {
      _versionCheckedAtUtc = DateTime.UtcNow;
      _logger.LogWarning(ex, "Reading the rights version failed; serving cached rights");
    }
    finally
    {
      _versionLock.Release();
    }
  }

  /// <returns>null when the rights have never been read successfully — the caller denies.</returns>
  private async Task<MapProto.EffectiveRightsResponse?> GetRightsAsync(
    ClaimsPrincipal user, string username, CancellationToken ct)
  {
    if (_byUser.TryGetValue(username, out var cached)
        && DateTime.UtcNow - cached.FetchedAtUtc < CacheTtl)
    {
      return cached.Rights;
    }

    var gate = _refreshLocks.GetOrAdd(username, _ => new SemaphoreSlim(1, 1));
    await gate.WaitAsync(ct);
    try
    {
      if (_byUser.TryGetValue(username, out cached)
          && DateTime.UtcNow - cached.FetchedAtUtc < CacheTtl)
      {
        return cached.Rights;
      }

      var generation = Volatile.Read(ref _generation);
      var fresh = await _map.GetEffectiveRightsAsync(
        new MapProto.EffectiveRightsRequest { Caller = CallerOf(user) },
        new CallOptions(
          deadline: DateTime.UtcNow.Add(MapCallTimeout),
          cancellationToken: ct));

      // Serve what was read, but only cache it if nothing was invalidated while it was in flight.
      if (Volatile.Read(ref _generation) == generation)
        _byUser[username] = new CachedRights(fresh, DateTime.UtcNow);

      return fresh;
    }
    catch (Exception ex)
    {
      _logger.LogWarning(ex, "Reading rights for '{Username}' failed", username);
      return _byUser.TryGetValue(username, out var stale) ? stale.Rights : null;
    }
    finally
    {
      gate.Release();
    }
  }

  private static MapProto.Caller CallerOf(ClaimsPrincipal user) => new()
  {
    Username = user.Identity?.Name ?? string.Empty,
    Roles = { user.Claims.Where(c => c.Type == ClaimTypes.Role).Select(c => c.Value) },
    Unrestricted = !AuthGate.KeycloakEnabled,
  };
}
