using System.Collections.Concurrent;
using System.Security.Claims;
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
/// a config round-trip on a hot path. Freshness comes from pub/sub invalidation
/// (<see cref="CameraPermissionSubscriber"/>); the TTL is only a backstop.
/// </summary>
public sealed class CameraAccessService(
  IPermissionManager permissions,
  ILogger<CameraAccessService> logger) : ICameraAccessService
{
  private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);

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

    await _modeLock.WaitAsync(ct);
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

      var fresh = await permissions.GetForUser(username, ct);
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
