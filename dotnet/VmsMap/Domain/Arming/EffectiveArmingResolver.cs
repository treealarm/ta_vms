using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using VmsMap.Clients;
using VmsMap.Db;

namespace VmsMap.Domain.Arming
{
  /// <summary>What a node's arming resolves to, and where the answer came from.</summary>
  public sealed record NodeArmingInfo(string ObjectId, bool Armed, string Source);

  /// <summary>
  /// Resolves the arming of a device node:
  /// <code>
  /// arm_mode = 'armed'    -> true
  /// arm_mode = 'disarmed' -> false
  /// arm_mode = 'inherit'  -> the nearest explicit armed property of the space the camera stands in
  ///                          (its reference replica's space and that space's ancestors), else the
  ///                          camera's own armed flag
  /// </code>
  /// The canonical tree hangs under the server, so a "zone" is not an ancestor of the node — it is
  /// where the camera stands, which belongs to its reference replica. A device right is anchored the
  /// same way (invariant 13), and for the same reason: the canonical node inherits nothing from the
  /// map.
  /// </summary>
  public sealed class EffectiveArmingResolver
  {
    public const string Explicit = "explicit";
    public const string Space = "space";
    public const string Camera = "camera";

    // The property an administrator puts on a space to arm it. The camera's own node carries the
    // same property as a mirror of camera_states.armed; a space is not in camera_states.
    public const string ArmedProperty = "armed";

    private readonly VmsMapDbContext _db;
    private readonly IDeviceSettingsSource _settings;
    private readonly ICameraStateSource _cameraState;

    public EffectiveArmingResolver(
      VmsMapDbContext db, IDeviceSettingsSource settings, ICameraStateSource cameraState)
    {
      _db = db;
      _settings = settings;
      _cameraState = cameraState;
    }

    public async Task<NodeArmingInfo?> ResolveAsync(string objectId, CancellationToken ct)
    {
      var guid = Utils.ConvertObjectIdToGuid(objectId);
      if (guid is null)
        return null;

      var node = await _db.DeviceNodes.AsNoTracking()
        .FirstOrDefaultAsync(n => n.object_id == guid.Value, ct);
      if (node is null)
        return null;

      var cameraId = node.source_id;
      var armMode = ArmInherit;

      // Only an input has a configured mode; a relay's settings row is constrained to inherit, and
      // a camera node has no part settings at all.
      if (string.Equals(node.part_kind, DeviceObjectId.DigitalInputPart, StringComparison.Ordinal))
      {
        var settings = await _settings.FindAsync(cameraId, node.part_kind, node.part_token, ct);
        armMode = settings?.ArmMode ?? ArmInherit;
      }

      if (string.Equals(armMode, ArmArmed, StringComparison.Ordinal))
        return new NodeArmingInfo(objectId, true, Explicit);
      if (string.Equals(armMode, ArmDisarmed, StringComparison.Ordinal))
        return new NodeArmingInfo(objectId, false, Explicit);

      if (await SpaceArmedAsync(cameraId, ct) is { } spaceArmed)
        return new NodeArmingInfo(objectId, spaceArmed, Space);

      var armed = await _cameraState.GetArmedAsync(cameraId, ct);
      return new NodeArmingInfo(objectId, armed, Camera);
    }

    /// <summary>
    /// Walks up from the reference replica's space. The nearest explicit armed property wins, so a
    /// disarmed floor inside an armed building is disarmed; a space with no property is skipped
    /// rather than read as false.
    /// </summary>
    private async Task<bool?> SpaceArmedAsync(string cameraId, CancellationToken ct)
    {
      var reference = await _db.CameraPlacements.AsNoTracking()
        .Where(p => p.camera_id == cameraId && p.is_reference)
        .Select(p => p.object_id)
        .FirstOrDefaultAsync(ct);

      if (reference == Guid.Empty)
        return null;

      var current = await _db.Markers.AsNoTracking()
        .Where(m => m.id == reference)
        .Select(m => m.parent_id)
        .FirstOrDefaultAsync(ct);

      var visited = new HashSet<Guid>();
      while (current is { } node && visited.Add(node))
      {
        var value = await _db.Properties.AsNoTracking()
          .Where(p => p.object_id == node && p.prop_name == ArmedProperty)
          .Select(p => p.str_val)
          .FirstOrDefaultAsync(ct);

        if (bool.TryParse(value, out var armed))
          return armed;

        current = await _db.Markers.AsNoTracking()
          .Where(m => m.id == node)
          .Select(m => m.parent_id)
          .FirstOrDefaultAsync(ct);
      }

      return null;
    }

    // Kept as constants rather than reaching into VmsCfg's rules: VmsMap must not link VmsCfg's
    // assembly for three strings, and these are the wire values the contract names.
    private const string ArmInherit = "inherit";
    private const string ArmArmed = "armed";
    private const string ArmDisarmed = "disarmed";
  }
}
