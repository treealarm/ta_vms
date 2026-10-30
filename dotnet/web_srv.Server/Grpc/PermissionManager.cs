using Grpc.Core;
using Google.Protobuf.WellKnownTypes;
using web_srv.Authorization;
using web_srv.Types.Dto;
using MapProto = Vms.Map;

namespace web_srv;

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
/// The compatibility face of the rights model for the camera-permissions page. The page edits a
/// per-user matrix of the five old flags; the rows live on camera nodes in VmsMap now. A blanket
/// grant becomes a right on every root node, which is what makes it cover cameras added later.
/// M4 replaces this page with the tree's own rights tab.
/// </summary>
public class PermissionManager(
  MapProto.MapService.MapServiceClient map,
  IRightsService rights) : IPermissionManager
{
  private static readonly TimeSpan MapCallTimeout = TimeSpan.FromSeconds(5);

  private static CallOptions Options(CancellationToken ct = default) =>
    new(deadline: DateTime.UtcNow.Add(MapCallTimeout), cancellationToken: ct);

  private static MapProto.Caller Admin => new() { Unrestricted = true };

  public async Task<List<UserCameraPermissionsDto>> ListAll(CancellationToken ct = default)
  {
    var all = await map.GetRightsAsync(new MapProto.ObjectIds(), Options(ct));
    var roots = await RootIdsAsync(ct);
    var cameraByObject = await CameraIdsByObjectAsync(all.Rights, ct);

    return all.Rights
      .GroupBy(r => r.Subject, StringComparer.Ordinal)
      .Select(group => Build(group.Key, group, roots, cameraByObject))
      .OrderBy(u => u.Username, StringComparer.Ordinal)
      .ToList();
  }

  public async Task<UserCameraPermissionsDto> GetForUser(
    string username, CancellationToken ct = default)
  {
    var all = await map.GetRightsAsync(new MapProto.ObjectIds(), Options(ct));
    var rows = all.Rights.Where(r => r.Subject == username).ToList();
    if (rows.Count == 0)
      return new UserCameraPermissionsDto { Username = username };

    var roots = await RootIdsAsync(ct);
    var cameraByObject = await CameraIdsByObjectAsync(rows, ct);
    return Build(username, rows, roots, cameraByObject);
  }

  public async Task<bool> ReplaceForUser(
    UserCameraPermissionsDto dto, CancellationToken ct = default)
  {
    // The page edits the whole matrix and applies it at once; a partial update protocol would
    // only add ways to disagree.
    var all = await map.GetRightsAsync(new MapProto.ObjectIds(), Options(ct));
    var existing = all.Rights.Where(r => r.Subject == dto.Username).ToList();
    if (existing.Count > 0)
    {
      var keys = new MapProto.RightKeyList();
      keys.Keys.AddRange(existing.Select(r => new MapProto.RightKey
      {
        ObjectId = r.ObjectId,
        Subject = r.Subject,
        Action = r.Action,
      }));
      await map.DeleteRightsAsync(keys, Options(ct));
    }

    var rows = new List<MapProto.Right>();

    if (dto.AllCameras is { } allCameras)
    {
      foreach (var root in await RootIdsAsync(ct))
        AddRows(rows, root, dto.Username, Actions(allCameras));
    }

    foreach (var (cameraId, flags) in dto.Cameras)
    {
      var node = await map.GetCameraNodeAsync(
        new MapProto.CameraRequest { CameraId = cameraId }, Options(ct));
      if (!node.Found)
        continue;

      AddRows(rows, node.Marker.Id, dto.Username, Actions(flags));
    }

    if (rows.Count > 0)
    {
      var request = new MapProto.RightList();
      request.Rights.AddRange(rows);
      var status = await map.UpdateRightsAsync(request, Options(ct));
      rights.Invalidate(dto.Username);
      return status.Success;
    }

    rights.Invalidate(dto.Username);
    return true;
  }

  public async Task<bool> GetAclEnforced(CancellationToken ct = default)
  {
    var response = await map.GetRightsModeAsync(new Empty(), Options(ct));
    return response.Mode == MapProto.RightsMode.Enforced;
  }

  public async Task<bool> SetAclEnforced(bool enforced, CancellationToken ct = default)
  {
    var response = await map.SetRightsModeAsync(new MapProto.SetRightsModeRequest
    {
      Mode = enforced ? MapProto.RightsMode.Enforced : MapProto.RightsMode.Open,
    }, Options(ct));

    rights.Invalidate(string.Empty);
    return response.Success;
  }

  private async Task<HashSet<string>> RootIdsAsync(CancellationToken ct)
  {
    var response = await map.GetByParentAsync(new MapProto.GetByParentRequest
    {
      ParentId = string.Empty,
      Count = 10_000,
      Caller = Admin,
    }, Options(ct));

    return response.Children.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
  }

  private async Task<Dictionary<string, string>> CameraIdsByObjectAsync(
    IEnumerable<MapProto.Right> rows, CancellationToken ct)
  {
    var ids = rows.Select(r => r.ObjectId).Distinct(StringComparer.Ordinal).ToList();
    if (ids.Count == 0)
      return new Dictionary<string, string>(StringComparer.Ordinal);

    var markers = await map.GetMarkersByIdsAsync(new MapProto.GetByIdsRequest
    {
      Ids = { ids },
      Caller = Admin,
    }, Options(ct));

    return markers.Markers
      .Where(m => !string.IsNullOrEmpty(m.CameraId))
      .ToDictionary(m => m.Id, m => m.CameraId, StringComparer.Ordinal);
  }

  private static UserCameraPermissionsDto Build(
    string username,
    IEnumerable<MapProto.Right> rows,
    HashSet<string> roots,
    Dictionary<string, string> cameraByObject)
  {
    CameraPermissionFlagsDto? blanket = null;
    var cameras = new Dictionary<string, CameraPermissionFlagsDto>(StringComparer.Ordinal);

    foreach (var row in rows)
    {
      var flags = Flags(row.Action);
      if (roots.Contains(row.ObjectId))
      {
        blanket = Merge(blanket, flags);
      }
      else if (cameraByObject.TryGetValue(row.ObjectId, out var cameraId))
      {
        cameras[cameraId] = Merge(cameras.GetValueOrDefault(cameraId), flags);
      }
    }

    return new UserCameraPermissionsDto
    {
      Username = username,
      AllCameras = blanket,
      Cameras = cameras,
    };
  }

  private static void AddRows(
    List<MapProto.Right> rows, string objectId, string subject, IEnumerable<string> actions)
  {
    foreach (var action in actions)
    {
      rows.Add(new MapProto.Right
      {
        ObjectId = objectId,
        Subject = subject,
        Action = action,
      });
    }
  }

  // A flag means the action plus `view`: without `view` the camera would not appear in the tree
  // the operator works on, which is not what the old per-camera checkbox meant.
  private static IEnumerable<string> Actions(CameraPermissionFlagsDto flags)
  {
    var actions = new List<string>();
    if (flags.ViewLive || flags.ViewArchive || flags.Ptz || flags.Control || flags.Export)
      actions.Add(RightActions.View);

    if (flags.ViewLive) actions.Add(RightActions.ViewLive);
    if (flags.ViewArchive) actions.Add(RightActions.ViewArchive);
    if (flags.Ptz) actions.Add(RightActions.Ptz);
    if (flags.Control) actions.Add(RightActions.Control);
    if (flags.Export) actions.Add(RightActions.Export);
    return actions;
  }

  private static CameraPermissionFlagsDto Flags(string action) => new()
  {
    ViewLive = action == RightActions.ViewLive,
    ViewArchive = action == RightActions.ViewArchive,
    Ptz = action == RightActions.Ptz,
    Control = action == RightActions.Control,
    Export = action == RightActions.Export,
  };

  private static CameraPermissionFlagsDto Merge(
    CameraPermissionFlagsDto? left, CameraPermissionFlagsDto right) => new()
  {
    ViewLive = (left?.ViewLive ?? false) || right.ViewLive,
    ViewArchive = (left?.ViewArchive ?? false) || right.ViewArchive,
    Ptz = (left?.Ptz ?? false) || right.Ptz,
    Control = (left?.Control ?? false) || right.Control,
    Export = (left?.Export ?? false) || right.Export,
  };
}
