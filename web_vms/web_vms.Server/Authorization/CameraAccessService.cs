using System.Collections.Concurrent;
using System.Security.Claims;
using Dapr.Client;
using web_vms.Authentication;
using web_vms.Types.Dto;

namespace web_vms.Authorization;

public interface ICameraAccessService
{
  /// <summary>False = denied. Never throws on a permission miss — callers decide 403 vs 404.</summary>
  Task<bool> IsAllowedAsync(ClaimsPrincipal user, string cameraId, CameraAction action, CancellationToken ct);

  /// <summary>
  /// Which cameras this action is permitted on. List endpoints filter with it; query-backed ones
  /// push it down as a filter so paging stays correct.
  /// </summary>
  Task<CameraScope> ScopeAsync(ClaimsPrincipal user, CameraAction action, CancellationToken ct);

  /// <summary>Drops one user's cached grants, or all of them when the username is empty.</summary>
  void Invalidate(string username);
}

/// <summary>
/// The single place that answers "may this caller touch this camera". Reads grants from VmsCfg
/// through <see cref="IPermissionManager"/> and caches them per user, because this sits on every
/// request — including per-camera state polls and archive segment fetches — and CLAUDE.md forbids
/// a config round-trip on a hot path.
///
/// Freshness comes from a version key VmsCfg rewrites on every permission or ACL-mode change: this
/// service re-reads it at most once a second and drops the whole cache when it moved. That is a
/// shared signal rather than a per-instance one, so every replica converges within a second — a
/// pub/sub subscription could not, since all replicas share one Dapr consumer group and only one of
/// them would ever be told. The TTL below stays as the backstop for a state store hiccup.
/// </summary>
public sealed class CameraAccessService(
  IPermissionManager permissions,
  DaprClient dapr,
  ILogger<CameraAccessService> logger) : ICameraAccessService
{
  private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);

  // Cheap enough to do on a hot path once per interval per replica, which bounds how long a
  // revoked grant can survive anywhere in the cluster. The state store is PostgreSQL, so this
  // is a primary-key lookup rather than the Redis GET it used to be — still one small indexed
  // read per second per replica, but worth remembering if the probe interval is ever shortened.
  private static readonly TimeSpan VersionProbeInterval = TimeSpan.FromSeconds(1);
  // The probe holds a process-wide lock, so it needs a bound of its own — see EnsureFreshAsync.
  private static readonly TimeSpan VersionProbeTimeout = TimeSpan.FromSeconds(2);
  private const string VersionKey = "cam-perm-version";

  // Read per instance, not into a static field: a missing variable in a static initializer surfaces
  // as a TypeInitializationException from DI, which says nothing about which variable is missing.
  private readonly string _stateStoreName =
    Environment.GetEnvironmentVariable("DAPR_STATE_STORE_NAME")
      ?? throw new InvalidOperationException("Required environment variable 'DAPR_STATE_STORE_NAME' is not set");

  private readonly SemaphoreSlim _versionLock = new(1, 1);
  private string? _lastSeenVersion;
  private DateTime _versionCheckedAtUtc = DateTime.MinValue;

  // Incremented by every invalidation; see the check around the cache write in GetGrantsAsync.
  private int _generation;

  private sealed record CachedGrants(UserCameraPermissionsDto Grants, DateTime FetchedAtUtc);

  private readonly ConcurrentDictionary<string, CachedGrants> _byUser = new(StringComparer.Ordinal);
  private readonly ConcurrentDictionary<string, SemaphoreSlim> _refreshLocks = new(StringComparer.Ordinal);

  private readonly SemaphoreSlim _modeLock = new(1, 1);
  private volatile bool _enforced;
  private bool _modeEverLoaded;
  private DateTime _modeFetchedAtUtc = DateTime.MinValue;

  public async Task<bool> IsAllowedAsync(
    ClaimsPrincipal user, string cameraId, CameraAction action, CancellationToken ct)
  {
    if (string.IsNullOrWhiteSpace(cameraId))
      return false;

    var scope = await ScopeAsync(user, action, ct);
    return scope.Contains(cameraId);
  }

  public async Task<CameraScope> ScopeAsync(ClaimsPrincipal user, CameraAction action, CancellationToken ct)
  {
    // No identity in this deployment at all — the policy handlers already pass everything through,
    // and there is nothing to key grants on.
    if (!AuthGate.KeycloakEnabled)
      return CameraScope.Everything;

    if (user.IsInRole(RoleConstants.Admin))
      return CameraScope.Everything;

    // From here on the answer is served from cache, so this is where it has to be proven current.
    await EnsureFreshAsync(ct);

    if (!await IsEnforcedAsync(ct))
      return CameraScope.Everything;

    var username = user.Identity?.Name;
    if (string.IsNullOrEmpty(username))
      return CameraScope.Nothing;

    var grants = await GetGrantsAsync(username, ct);
    if (grants is null)
      return CameraScope.Nothing;

    if (Allows(grants.AllCameras, action))
      return CameraScope.Everything;

    var ids = grants.Cameras
      .Where(kv => Allows(kv.Value, action))
      .Select(kv => kv.Key)
      .ToHashSet(StringComparer.Ordinal);

    return CameraScope.Of(ids);
  }

  public void Invalidate(string username)
  {
    // Bumped before the removal so a fetch that is already in flight — started against the old
    // grants, landing after the flush — notices it is stale and does not re-insert what was just
    // dropped, which would keep a revoked grant alive for another full TTL.
    Interlocked.Increment(ref _generation);

    if (string.IsNullOrEmpty(username))
    {
      _byUser.Clear();
      _modeFetchedAtUtc = DateTime.MinValue;
      logger.LogInformation("Camera permission cache flushed");
      return;
    }

    _byUser.TryRemove(username, out _);
    logger.LogInformation("Camera permissions for '{Username}' invalidated", username);
  }

  /// <summary>
  /// Drops the cache when VmsCfg has rewritten the shared permission version since the last probe.
  /// Never throws: a state store that cannot be read leaves the cache in place, so an infrastructure
  /// hiccup slows propagation down to <see cref="CacheTtl"/> instead of locking operators out.
  /// </summary>
  private async Task EnsureFreshAsync(CancellationToken ct)
  {
    if (DateTime.UtcNow - _versionCheckedAtUtc < VersionProbeInterval)
      return;

    // Never queue behind another request's probe. This sits on every authorized request, so a lock
    // held across a state store call turns one slow read into a stall of the whole API — and
    // /api/health, the only endpoint without [CameraAccess], keeps answering in a millisecond and
    // hides it. A request that finds a probe already running serves the cached grants instead:
    // CacheTtl is the correctness backstop, this only decides how fast a revocation propagates.
    if (!await _versionLock.WaitAsync(0, ct))
      return;

    try
    {
      if (DateTime.UtcNow - _versionCheckedAtUtc < VersionProbeInterval)
        return;

      // Bounded independently of the caller's token: the lock is held across this call, so an
      // unbounded read would also keep every later probe out for as long as it hung.
      using var probe = CancellationTokenSource.CreateLinkedTokenSource(ct);
      probe.CancelAfter(VersionProbeTimeout);

      var version = await dapr.GetStateAsync<string>(_stateStoreName, VersionKey, cancellationToken: probe.Token) ?? string.Empty;
      _versionCheckedAtUtc = DateTime.UtcNow;

      // First read only records where the counter stands — the cache is empty anyway, and flushing
      // on it would throw away every replica's warm cache on restart for nothing.
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
      // Back off exactly as if the probe had succeeded, so a state store outage doesn't turn every
      // single request into a failing round-trip. A probe that hit VersionProbeTimeout lands here
      // too, which is what it is for: it is a stale read, not a cancelled request.
      _versionCheckedAtUtc = DateTime.UtcNow;
      logger.LogWarning(ex, "Reading the camera permission version failed; serving cached grants");
    }
    finally
    {
      _versionLock.Release();
    }
  }

  private static bool Allows(CameraPermissionFlagsDto? flags, CameraAction action) => action switch
  {
    _ when flags is null    => false,
    CameraAction.ViewLive    => flags.ViewLive,
    CameraAction.ViewArchive => flags.ViewArchive,
    CameraAction.Ptz         => flags.Ptz,
    CameraAction.Control     => flags.Control,
    CameraAction.Export      => flags.Export,
    _                        => false,
  };

  private async Task<bool> IsEnforcedAsync(CancellationToken ct)
  {
    if (DateTime.UtcNow - _modeFetchedAtUtc < CacheTtl)
      return _enforced;

    // Same reasoning as the version probe above: whoever gets there first refreshes the mode, and
    // everyone else keeps serving the last known one rather than lining up behind a network call.
    if (!await _modeLock.WaitAsync(0, ct))
      return _enforced;

    try
    {
      if (DateTime.UtcNow - _modeFetchedAtUtc < CacheTtl)
        return _enforced;

      _enforced = await permissions.GetAclEnforced(ct);
      _modeEverLoaded = true;
      _modeFetchedAtUtc = DateTime.UtcNow;
    }
    catch (Exception ex)
    {
      // Keep serving the last known mode: a VmsCfg hiccup must neither open a locked-down system
      // nor black out an open one. Until it has ever been read, assume the safer of the two.
      if (!_modeEverLoaded)
        _enforced = true;
      logger.LogWarning(ex, "Reading the camera ACL mode failed; keeping enforced={Enforced}", _enforced);
      _modeFetchedAtUtc = DateTime.UtcNow;
    }
    finally
    {
      _modeLock.Release();
    }

    return _enforced;
  }

  /// <returns>null when the grants have never been read successfully — the caller denies.</returns>
  private async Task<UserCameraPermissionsDto?> GetGrantsAsync(string username, CancellationToken ct)
  {
    if (_byUser.TryGetValue(username, out var cached) && DateTime.UtcNow - cached.FetchedAtUtc < CacheTtl)
      return cached.Grants;

    var gate = _refreshLocks.GetOrAdd(username, _ => new SemaphoreSlim(1, 1));
    await gate.WaitAsync(ct);
    try
    {
      if (_byUser.TryGetValue(username, out cached) && DateTime.UtcNow - cached.FetchedAtUtc < CacheTtl)
        return cached.Grants;

      var generation = Volatile.Read(ref _generation);
      var fresh = await permissions.GetForUser(username, ct);

      // Serve what was read, but only cache it if nothing was invalidated while it was in flight —
      // otherwise this write would restore grants the flush had just deleted.
      if (Volatile.Read(ref _generation) == generation)
        _byUser[username] = new CachedGrants(fresh, DateTime.UtcNow);

      return fresh;
    }
    catch (Exception ex)
    {
      // A stale grant set beats logging an operator out mid-shift over a transient failure; with no
      // grant set at all there is nothing to serve, so deny.
      logger.LogWarning(ex, "Reading camera permissions for '{Username}' failed", username);
      return _byUser.TryGetValue(username, out var stale) ? stale.Grants : null;
    }
    finally
    {
      gate.Release();
    }
  }
}
