using Microsoft.EntityFrameworkCore;
using Vms.Cfg;

namespace VmsCfg;

/// <summary>
/// Per-camera user grants and the ACL mode switch. Usernames are Keycloak preferred_usernames;
/// VmsCfg never validates them against Keycloak — a grant for a user that no longer exists is
/// simply never matched.
/// </summary>
public sealed class PermissionDb(IDbContextFactory<VmsCfgDbContext> dbContextFactory)
{
  private readonly IDbContextFactory<VmsCfgDbContext> _dbContextFactory = dbContextFactory;

  /// <param name="username">Empty or null = every user holding at least one grant.</param>
  public async Task<List<UserCameraPermissions>> ListAsync(string? username, CancellationToken ct)
  {
    await using var db = await _dbContextFactory.CreateDbContextAsync(ct);

    var wanted = string.IsNullOrEmpty(username) ? null : username;

    var perCamera = await db.UserCameraPermissions
      .Where(p => wanted == null || p.Username == wanted)
      .OrderBy(p => p.Username).ThenBy(p => p.CameraId)
      .ToListAsync(ct);

    var blanket = await db.UserAllCamerasPermissions
      .Where(p => wanted == null || p.Username == wanted)
      .ToListAsync(ct);

    var byUser = new Dictionary<string, UserCameraPermissions>();

    UserCameraPermissions ForUser(string name)
    {
      if (!byUser.TryGetValue(name, out var entry))
      {
        entry = new UserCameraPermissions { Username = name };
        byUser[name] = entry;
      }
      return entry;
    }

    foreach (var row in blanket)
      ForUser(row.Username).AllCameras = ToFlags(row.ViewLive, row.ViewArchive, row.Ptz, row.Control, row.Export);

    foreach (var row in perCamera)
    {
      ForUser(row.Username).Cameras.Add(new CameraPermission
      {
        CameraId = row.CameraId,
        Flags = ToFlags(row.ViewLive, row.ViewArchive, row.Ptz, row.Control, row.Export),
      });
    }

    return [.. byUser.Values.OrderBy(u => u.Username, StringComparer.Ordinal)];
  }

  /// <summary>
  /// Replaces everything this user is granted, in one transaction — the admin UI edits the whole
  /// matrix and applies it at once, so a partial-update protocol would only add ways to disagree.
  /// Entries with no flags set are dropped rather than stored: an all-false row and a missing row
  /// mean the same thing.
  /// </summary>
  public async Task ReplaceUserAsync(UserCameraPermissions permissions, CancellationToken ct)
  {
    await using var db = await _dbContextFactory.CreateDbContextAsync(ct);
    await using var tx = await db.Database.BeginTransactionAsync(ct);

    var username = permissions.Username;

    db.UserCameraPermissions.RemoveRange(
      await db.UserCameraPermissions.Where(p => p.Username == username).ToListAsync(ct));
    db.UserAllCamerasPermissions.RemoveRange(
      await db.UserAllCamerasPermissions.Where(p => p.Username == username).ToListAsync(ct));
    await db.SaveChangesAsync(ct);

    if (permissions.AllCameras is { } all && HasAny(all))
    {
      db.UserAllCamerasPermissions.Add(new DbUserAllCamerasPermission
      {
        Username    = username,
        ViewLive    = all.ViewLive,
        ViewArchive = all.ViewArchive,
        Ptz         = all.Ptz,
        Control     = all.Control,
        Export      = all.Export,
      });
    }

    foreach (var camera in permissions.Cameras)
    {
      if (camera.Flags is null || !HasAny(camera.Flags))
        continue;

      db.UserCameraPermissions.Add(new DbUserCameraPermission
      {
        Username    = username,
        CameraId    = camera.CameraId,
        ViewLive    = camera.Flags.ViewLive,
        ViewArchive = camera.Flags.ViewArchive,
        Ptz         = camera.Flags.Ptz,
        Control     = camera.Flags.Control,
        Export      = camera.Flags.Export,
      });
    }

    await db.SaveChangesAsync(ct);
    await tx.CommitAsync(ct);
  }

  public async Task<CameraAclMode> GetModeAsync(CancellationToken ct)
  {
    await using var db = await _dbContextFactory.CreateDbContextAsync(ct);
    var setting = await db.CameraAclSettings.FirstOrDefaultAsync(ct);
    return setting?.Mode == CameraAclModes.Enforced ? CameraAclMode.CameraAclEnforced : CameraAclMode.CameraAclOpen;
  }

  public async Task SetModeAsync(CameraAclMode mode, CancellationToken ct)
  {
    await using var db = await _dbContextFactory.CreateDbContextAsync(ct);
    var value = mode == CameraAclMode.CameraAclEnforced ? CameraAclModes.Enforced : CameraAclModes.Open;

    var setting = await db.CameraAclSettings.FirstOrDefaultAsync(ct);
    if (setting is null)
      db.CameraAclSettings.Add(new DbCameraAclSetting { Id = true, Mode = value });
    else
      setting.Mode = value;

    await db.SaveChangesAsync(ct);
  }

  private static CameraPermissionFlags ToFlags(bool live, bool archive, bool ptz, bool control, bool export) =>
    new() { ViewLive = live, ViewArchive = archive, Ptz = ptz, Control = control, Export = export };

  private static bool HasAny(CameraPermissionFlags f) =>
    f.ViewLive || f.ViewArchive || f.Ptz || f.Control || f.Export;
}
