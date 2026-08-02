using Grpc.Core;
using Google.Protobuf.WellKnownTypes;
using Vms.Cfg;
using web_vms.Types.Dto;

namespace web_vms;

public interface IPermissionManager
{
  Task<List<UserCameraPermissionsDto>> ListAll(CancellationToken ct = default);
  /// <summary>Never null: a user with no grants comes back as an empty set, not a missing one.</summary>
  Task<UserCameraPermissionsDto> GetForUser(string username, CancellationToken ct = default);
  Task<bool> ReplaceForUser(UserCameraPermissionsDto dto, CancellationToken ct = default);
  Task<bool> GetAclEnforced(CancellationToken ct = default);
  Task<bool> SetAclEnforced(bool enforced, CancellationToken ct = default);
}

/// <summary>
/// Thin gateway-side wrapper over VmsCfg's permission RPCs. Deliberately cache-free — the caching
/// that matters lives in the authorization layer, which also owns the pub/sub invalidation.
/// </summary>
public class PermissionManager(CfgService.CfgServiceClient cfgClient) : IPermissionManager
{
  private static readonly TimeSpan CfgCallTimeout = TimeSpan.FromSeconds(5);

  private static CallOptions CfgOptions(CancellationToken ct = default) =>
    new(deadline: DateTime.UtcNow.Add(CfgCallTimeout), cancellationToken: ct);

  public async Task<List<UserCameraPermissionsDto>> ListAll(CancellationToken ct = default)
  {
    var response = await cfgClient.ListCameraPermissionsAsync(new ListCameraPermissionsRequest(), CfgOptions(ct));
    return [.. response.Users.Select(ToDto)];
  }

  public async Task<UserCameraPermissionsDto> GetForUser(string username, CancellationToken ct = default)
  {
    var response = await cfgClient.ListCameraPermissionsAsync(
      new ListCameraPermissionsRequest { Username = username }, CfgOptions(ct));

    return response.Users.Count > 0
      ? ToDto(response.Users[0])
      : new UserCameraPermissionsDto { Username = username };
  }

  public async Task<bool> ReplaceForUser(UserCameraPermissionsDto dto, CancellationToken ct = default)
  {
    var permissions = new UserCameraPermissions { Username = dto.Username };
    if (dto.AllCameras is not null)
      permissions.AllCameras = ToProto(dto.AllCameras);

    foreach (var (cameraId, flags) in dto.Cameras)
      permissions.Cameras.Add(new CameraPermission { CameraId = cameraId, Flags = ToProto(flags) });

    var response = await cfgClient.SetUserCameraPermissionsAsync(
      new SetUserCameraPermissionsRequest { Permissions = permissions }, CfgOptions(ct));
    return response.Success;
  }

  public async Task<bool> GetAclEnforced(CancellationToken ct = default)
  {
    var response = await cfgClient.GetCameraAclModeAsync(new Empty(), CfgOptions(ct));
    return response.Mode == CameraAclMode.CameraAclEnforced;
  }

  public async Task<bool> SetAclEnforced(bool enforced, CancellationToken ct = default)
  {
    var response = await cfgClient.SetCameraAclModeAsync(
      new SetCameraAclModeRequest
      {
        Mode = enforced ? CameraAclMode.CameraAclEnforced : CameraAclMode.CameraAclOpen,
      },
      CfgOptions(ct));
    return response.Success;
  }

  private static UserCameraPermissionsDto ToDto(UserCameraPermissions p) => new()
  {
    Username   = p.Username,
    AllCameras = p.AllCameras is null ? null : ToDto(p.AllCameras),
    Cameras    = p.Cameras
      .Where(c => c.Flags is not null)
      .ToDictionary(c => c.CameraId, c => ToDto(c.Flags)),
  };

  private static CameraPermissionFlagsDto ToDto(CameraPermissionFlags f) => new()
  {
    ViewLive    = f.ViewLive,
    ViewArchive = f.ViewArchive,
    Ptz         = f.Ptz,
    Control     = f.Control,
    Export      = f.Export,
  };

  private static CameraPermissionFlags ToProto(CameraPermissionFlagsDto f) => new()
  {
    ViewLive    = f.ViewLive,
    ViewArchive = f.ViewArchive,
    Ptz         = f.Ptz,
    Control     = f.Control,
    Export      = f.Export,
  };
}
