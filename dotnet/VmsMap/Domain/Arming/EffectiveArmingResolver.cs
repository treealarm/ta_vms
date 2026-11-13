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
  /// <summary>What a node's arming resolves to, and where the answer came from. `SourceObjectId`
  /// names the node whose property settled it — the zone a browser groups alarms by — and is empty
  /// when the answer came from the part's own setting or the camera's flag.</summary>
  public sealed record NodeArmingInfo(
    string ObjectId, bool Armed, string Source, string SourceObjectId = "");

  /// <summary>
  /// One device part whose arming is asked for: its node, and the source it belongs to. A list of
  /// them is resolved in one pass, so a page of a hundred sensors does not cost a hundred round
  /// trips to VmsCfg.
  /// </summary>
  public sealed record ArmingSubject(
    string ObjectId, string SourceId, string PartKind, string PartToken);

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
    /// <summary>A container took the answer from an ancestor rather than from itself.</summary>
    public const string Inherited = "inherited";
    /// <summary>Nothing above the container is set: it follows whoever is above it, and nobody is.</summary>
    public const string None = "none";

    // The property an administrator puts on a space to arm it. The camera's own node carries the
    // same property as a mirror of camera_states.armed; a space is not in camera_states.
    public const string ArmedProperty = ArmMode.ArmedProperty;

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

      var resolved = await ResolveManyAsync(
        [new ArmingSubject(objectId, node.source_id, node.part_kind, node.part_token)], ct);
      return resolved.GetValueOrDefault(objectId);
    }

    /// <summary>
    /// The same scheme for a page of parts at once: the configured mode, then the nearest armed
    /// property above the camera's reference replica, then the camera's own flag. Settings and
    /// camera flags are read in bulk, and the tree is walked one level at a time for all cameras
    /// together, so the cost is the tree's depth, not the page's length.
    /// </summary>
    public async Task<Dictionary<string, NodeArmingInfo>> ResolveManyAsync(
      IReadOnlyCollection<ArmingSubject> subjects, CancellationToken ct)
    {
      var result = new Dictionary<string, NodeArmingInfo>(StringComparer.Ordinal);
      if (subjects.Count == 0)
        return result;

      var cameraIds = subjects
        .Select(s => s.SourceId)
        .Distinct(StringComparer.Ordinal)
        .ToList();
      var settings = await _settings.ListManyAsync(cameraIds, ct);

      // The mode each part is configured with. Only an input has one; a relay's settings row is
      // constrained to inherit, and a camera node has no part settings at all.
      var modes = new Dictionary<(string, string), string>();
      foreach (var subject in subjects)
      {
        if (!string.Equals(subject.PartKind, DeviceObjectId.DigitalInputPart, StringComparison.Ordinal))
          continue;

        var configured = settings.GetValueOrDefault(subject.SourceId)?.FirstOrDefault(s =>
          string.Equals(s.Kind, subject.PartKind, StringComparison.Ordinal)
          && string.Equals(s.Token, subject.PartToken, StringComparison.Ordinal));
        if (configured is not null)
          modes[(subject.SourceId, subject.PartToken)] = configured.ArmMode;
      }

      var inheriting = new List<ArmingSubject>();
      foreach (var subject in subjects)
      {
        var mode = modes.GetValueOrDefault((subject.SourceId, subject.PartToken)) ?? ArmMode.Inherit;
        if (string.Equals(mode, ArmMode.Armed, StringComparison.Ordinal))
          result[subject.ObjectId] = new NodeArmingInfo(subject.ObjectId, true, Explicit);
        else if (string.Equals(mode, ArmMode.Disarmed, StringComparison.Ordinal))
          result[subject.ObjectId] = new NodeArmingInfo(subject.ObjectId, false, Explicit);
        else
          inheriting.Add(subject);
      }

      if (inheriting.Count == 0)
        return result;

      var inheritCameraIds = inheriting
        .Select(s => s.SourceId)
        .Distinct(StringComparer.Ordinal)
        .ToList();
      var spaceArmed = await SpaceArmedManyAsync(inheritCameraIds, ct);

      var needCamera = inheritCameraIds
        .Where(id => spaceArmed.GetValueOrDefault(id) is null)
        .ToList();
      var cameraArmed = needCamera.Count == 0
        ? new Dictionary<string, bool>(StringComparer.Ordinal)
        : await _cameraState.GetArmedManyAsync(needCamera, ct);

      foreach (var subject in inheriting)
      {
        if (spaceArmed.GetValueOrDefault(subject.SourceId) is { } answer)
        {
          result[subject.ObjectId] = new NodeArmingInfo(
            subject.ObjectId, answer.Armed, Space, Utils.ConvertGuidToObjectId(answer.ObjectId));
        }
        else
        {
          result[subject.ObjectId] = new NodeArmingInfo(
            subject.ObjectId, cameraArmed.GetValueOrDefault(subject.SourceId), Camera);
        }
      }

      return result;
    }

    /// <summary>Where one camera's inherited answer came from: the armed value and the node that
    /// carries it, so the caller can name the zone rather than only say "space".</summary>
    private sealed record SpaceAnswer(bool Armed, Guid ObjectId);

    /// <summary>
    /// Walks up from each camera's reference replica. The nearest explicit armed property wins, so
    /// a disarmed floor inside an armed building is disarmed; a space with no property is skipped
    /// rather than read as false. One query per tree level covers every camera still looking.
    /// </summary>
    private async Task<Dictionary<string, SpaceAnswer?>> SpaceArmedManyAsync(
      IReadOnlyCollection<string> cameraIds, CancellationToken ct)
    {
      var result = new Dictionary<string, SpaceAnswer?>(StringComparer.Ordinal);

      var references = await _db.CameraPlacements.AsNoTracking()
        .Where(p => cameraIds.Contains(p.camera_id) && p.is_reference)
        .Select(p => new { p.camera_id, p.object_id })
        .ToListAsync(ct);

      // The walk starts at the replica's space, not at the replica itself: the picture is not a
      // container and never carries the property.
      var replicaIds = references.Select(r => r.object_id).Distinct().ToList();
      var fronts = await _db.Markers.AsNoTracking()
        .Where(m => replicaIds.Contains(m.id))
        .Select(m => new { m.id, m.parent_id })
        .ToListAsync(ct);
      var parentOf = fronts
        .Where(f => f.parent_id.HasValue)
        .ToDictionary(f => f.id, f => f.parent_id!.Value);

      // camera -> the node whose ancestors are still being read, and the nodes already visited.
      var current = new Dictionary<string, Guid>(StringComparer.Ordinal);
      var visited = new Dictionary<string, HashSet<Guid>>(StringComparer.Ordinal);
      foreach (var reference in references)
      {
        if (parentOf.TryGetValue(reference.object_id, out var space))
        {
          current.TryAdd(reference.camera_id, space);
          visited[reference.camera_id] = [space];
        }
      }

      while (current.Count > 0)
      {
        var level = current.Values.Distinct().ToList();
        var values = await _db.Properties.AsNoTracking()
          .Where(p => level.Contains(p.object_id) && p.prop_name == ArmedProperty)
          .Select(p => new { p.object_id, p.str_val })
          .ToListAsync(ct);
        var byNode = values
          .GroupBy(v => v.object_id)
          .ToDictionary(g => g.Key, g => g.First().str_val);

        foreach (var cameraId in current.Keys.ToList())
        {
          if (byNode.TryGetValue(current[cameraId], out var value)
              && bool.TryParse(value, out var armed))
          {
            result[cameraId] = new SpaceAnswer(armed, current[cameraId]);
            current.Remove(cameraId);
          }
        }

        if (current.Count == 0)
          break;

        var stillLooking = current.Values.Distinct().ToList();
        var links = await _db.Markers.AsNoTracking()
          .Where(m => stillLooking.Contains(m.id))
          .Select(m => new { m.id, m.parent_id })
          .ToListAsync(ct);
        var nextOf = links
          .Where(l => l.parent_id.HasValue)
          .ToDictionary(l => l.id, l => l.parent_id!.Value);

        foreach (var cameraId in current.Keys.ToList())
        {
          var node = current[cameraId];
          if (nextOf.TryGetValue(node, out var parent) && visited[cameraId].Add(parent))
            current[cameraId] = parent;
          else
            current.Remove(cameraId); // the root, or a cycle the data should not have
        }
      }

      foreach (var cameraId in cameraIds)
        result.TryAdd(cameraId, null);

      return result;
    }

    /// <summary>
    /// The same question for a container — a space, or a zone drawn on the map. The walk starts at
    /// the node itself: a container's own property is what an operator set, and an ancestor's is
    /// what it follows.
    /// </summary>
    public async Task<NodeArmingInfo?> ResolveSpaceAsync(string objectId, CancellationToken ct)
    {
      var guid = Utils.ConvertObjectIdToGuid(objectId);
      if (guid is null)
        return null;

      var start = await _db.Markers.AsNoTracking()
        .FirstOrDefaultAsync(m => m.id == guid.Value, ct);
      if (start is null)
        return null;

      // The tree's depth, not its size: one query per level, and a visited set in case the data
      // ever holds a cycle.
      var visited = new HashSet<Guid>();
      Guid? current = start.id;
      var here = true;
      while (current is { } node && visited.Add(node))
      {
        var value = await _db.Properties.AsNoTracking()
          .Where(p => p.object_id == node && p.prop_name == ArmedProperty)
          .Select(p => p.str_val)
          .FirstOrDefaultAsync(ct);
        if (bool.TryParse(value, out var armed))
        {
          return new NodeArmingInfo(
            objectId, armed, here ? Explicit : Inherited, Utils.ConvertGuidToObjectId(node));
        }

        current = (await _db.Markers.AsNoTracking()
          .Where(m => m.id == node)
          .Select(m => m.parent_id)
          .FirstOrDefaultAsync(ct));
        here = false;
      }

      return new NodeArmingInfo(objectId, false, None);
    }
  }
}
