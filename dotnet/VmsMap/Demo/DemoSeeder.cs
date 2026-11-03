using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Vms.Map;
using VmsMap.Clients;
using VmsMap.Db;
using VmsMap.Domain;

namespace VmsMap.Demo
{
  /// Applies a `DemoWorld` to the map database and takes it away again. Everything it writes is
  /// recorded in `demo_object` under the world's logical key, so a second install updates the same
  /// rows and a removal deletes only those — a user's own node next to the world is never named by
  /// it and never touched.
  ///
  /// The whole install is one transaction under an advisory lock: two replicas starting with
  /// `DEMO_CONFIG` set must not each create half a world.
  public sealed class DemoSeeder
  {
    // hashed into the lock by PostgreSQL; any stable number does, as long as it is ours alone.
    private const long LockKey = 0x74615f64656d6f31; // "ta_demo1"

    private static readonly string[] DeleteOrder = ["object", "camera", "space", "type", "level", "file"];

    private readonly VmsMapDbContext _db;
    private readonly IMapUpdateService _updates;
    private readonly IDiagramServiceInternal _diagrams;
    private readonly IDiagramTypeService _types;
    private readonly ILevelService _levels;
    private readonly IStatesUpdateService _states;
    private readonly IRightServiceInternal _rights;
    private readonly IGeoService _geo;
    private readonly FileService _files;
    private readonly VisualService _visuals;
    private readonly SpatialDb _spatial;
    private readonly ICameraCatalog _cameras;

    public DemoSeeder(
      VmsMapDbContext db,
      IMapUpdateService updates,
      IDiagramServiceInternal diagrams,
      IDiagramTypeService types,
      ILevelService levels,
      IStatesUpdateService states,
      IRightServiceInternal rights,
      IGeoService geo,
      FileService files,
      VisualService visuals,
      SpatialDb spatial,
      ICameraCatalog cameras)
    {
      _db = db;
      _updates = updates;
      _diagrams = diagrams;
      _types = types;
      _levels = levels;
      _states = states;
      _rights = rights;
      _geo = geo;
      _files = files;
      _visuals = visuals;
      _spatial = spatial;
      _cameras = cameras;
    }

    public async Task<DemoStatus> GetStatusAsync(string key, CancellationToken ct)
    {
      var config = await _db.DemoConfigs.AsNoTracking()
        .FirstOrDefaultAsync(c => c.demo_key == key, ct);
      var entities = await _db.DemoObjects.AsNoTracking()
        .CountAsync(o => o.demo_key == key, ct);

      return new DemoStatus
      {
        Key = key,
        Installed = config is not null,
        Version = config?.version ?? 0,
        Entities = entities,
      };
    }

    /// Writes the world: every entity is upserted under the id the registry remembers (or the id
    /// derived from its key), and whatever the registry holds that the world no longer names is
    /// deleted. Running it twice leaves the same rows with the same ids.
    public async Task<DemoReport> InstallAsync(DemoWorld world, CancellationToken ct)
    {
      await using var tx = await BeginAsync(ct);
      await LockAsync(ct);

      var registered = await LoadRegistryAsync(world.Key, ct);
      var ids = new Dictionary<string, Guid>(registered, StringComparer.Ordinal);
      var keep = new HashSet<string>(StringComparer.Ordinal);
      var report = new DemoReport { Key = world.Key, Version = world.Version };

      // A world that names an entity before writing it is a bug in the model, not in the database.
      Guid Required(string key) => ids.TryGetValue(key, out var id)
        ? id
        : throw new InvalidOperationException($"The demo world names '{key}' before it is written");

      void Written(string key)
      {
        if (registered.ContainsKey(key)) report.Updated += 1;
        else report.Created += 1;
      }

      Guid IdOf(string key) => ids.TryGetValue(key, out var known) ? known : DemoIds.For(key);

      // ── Files: plan pictures, type backgrounds, icons ───────────────────────────────────────
      foreach (var file in world.Files)
      {
        var id = IdOf(file.Key);
        await _files.PutAsync(id, file.Mime, file.Content);
        ids[file.Key] = id;
        keep.Add(file.Key);
        Written(file.Key);
      }

      // ── Types: the picture behind a diagram and the regions it offers ───────────────────────
      foreach (var type in world.Types)
      {
        var id = IdOf(type.Key);
        var dto = new DiagramTypeDTO
        {
          id = id.ToString(),
          name = type.Name,
          file_id = type.BackgroundKey is null ? null : IdOf(type.BackgroundKey).ToString(),
          regions = type.Regions.Select(region => new DiagramTypeRegionDTO
          {
            region_key = region.Key,
            geometry = ToCoord(region.Geometry),
            styles = region.Styles?.ToDictionary(pair => pair.Key, pair => pair.Value),
          }).ToList(),
        };

        await _types.UpdateListAsync([dto]);
        ids[type.Key] = id;
        keep.Add(type.Key);
        Written(type.Key);
      }

      // ── Spaces, parents first: a plan needs the space it lies in ────────────────────────────
      foreach (var space in ParentsFirst(world.Spaces, s => s.Key, s => s.ParentKey))
      {
        var proto = new Space
        {
          Id = ids.TryGetValue(space.Key, out var known) ? known.ToString() : string.Empty,
          ParentId = space.ParentKey is null ? string.Empty : Required(space.ParentKey).ToString(),
          Name = space.Name,
          Kind = space.Kind,
          RealCoordinates = space.RealCoordinates,
          Ordinal = space.Ordinal,
          ElevationM = space.ElevationM,
          View = new GeoBounds
          {
            SouthWest = new Coordinate { Lat = space.South, Lon = space.West },
            NorthEast = new Coordinate { Lat = space.North, Lon = space.East },
          },
        };

        if (space.Footprint is not null)
        {
          proto.Footprint.AddRange(space.Footprint
            .Select(point => new Coordinate { Lat = point.Lat, Lon = point.Lon }));
        }

        var saved = await _spatial.UpsertSpaceAsync(proto, ct);
        ids[space.Key] = Guid.Parse(saved.Id);
        keep.Add(space.Key);
        Written(space.Key);

        if (space.Plan is not null)
        {
          await _spatial.SavePlanAsync(new SpacePlanUploadHeader
          {
            SpaceId = saved.Id,
            Mime = world.Files.Single(file => file.Key == space.Plan.FileKey).Mime,
            WidthPx = space.Plan.WidthPx,
            HeightPx = space.Plan.HeightPx,
            Corners = new PlanCorners
            {
              TopLeft = ToCoordinate(space.Plan.TopLeft),
              TopRight = ToCoordinate(space.Plan.TopRight),
              BottomLeft = ToCoordinate(space.Plan.BottomLeft),
            },
          }, world.Files.Single(file => file.Key == space.Plan.FileKey).Content, ct);
        }
      }

      // ── Nodes: mnemoschemes, their children and the figures they are drawn at ───────────────
      foreach (var node in ParentsFirst(world.Nodes, n => n.Key, n => n.ParentKey))
      {
        var id = IdOf(node.Key);
        var parentId = node.ParentKey is null ? null : Required(node.ParentKey).ToString();

        await _updates.UpdateObjects([
          new BaseMarkerDTO { id = id.ToString(), parent_id = parentId, name = node.Name }]);

        if (node.TypeKey is not null)
        {
          await _diagrams.UpdateListAsync([new DiagramDTO
          {
            id = id.ToString(),
            dgr_type = Required(node.TypeKey).ToString(),
            region_id = node.RegionKey,
            geometry = node.Canvas is { } canvas ? ToCoord(canvas) : null,
            file_id = node.IconKey is null ? null : Required(node.IconKey).ToString(),
          }]);
        }

        if (node.VisualKey is not null)
        {
          var icon = world.Files.Single(file => file.Key == node.VisualKey);
          await _visuals.UpdateListAsync([new VisualDTO
          {
            object_id = id.ToString(),
            image = icon.Content,
            image_width = node.VisualWidth,
            image_height = node.VisualHeight,
          }]);
        }

        if (node.At is { } at)
        {
          await _geo.CreateGeo([new FigureGeoDTO
          {
            id = id.ToString(),
            parent_id = parentId,
            name = node.Name,
            geometry = new GeometryCircleDTO(new Geo2DCoordDTO { Lat = at.Lat, Lon = at.Lon }),
            zoom_level = node.ZoomLevel,
          }]);
        }
        else
        {
          // A node that moved from a canvas into a diagram (or never had a place on one) must not
          // keep the point it used to stand at.
          await _geo.RemoveAsync([id.ToString()]);
        }

        ids[node.Key] = id;
        keep.Add(node.Key);
        Written(node.Key);
      }

      // ── Cameras: placed where the world wants them, skipped when the deployment lacks one ──
      foreach (var placement in world.Placements)
      {
        var key = $"camera/{placement.CameraId}";
        var spaceId = Required(placement.SpaceKey);
        var camera = await _cameras.GetAsync(placement.CameraId, ct);

        if (camera is null)
        {
          // Kept in the registry if it was there: whatever placement the camera still has was put
          // there by an earlier install, and a removal must find it.
          if (registered.TryGetValue(key, out var earlier)) ids[key] = earlier;
          report.SkippedCameras.Add(placement.CameraId);
          continue;
        }

        await _spatial.UpsertPlacementAsync(new CameraPlacement
        {
          CameraId = placement.CameraId,
          SpaceId = spaceId.ToString(),
          IsReference = placement.IsReference,
          Lat = placement.Lat,
          Lon = placement.Lon,
          HeightM = placement.HeightM,
          HeadingDeg = placement.HeadingDeg,
          HfovDeg = placement.HfovDeg,
          RangeM = placement.RangeM,
        }, ct);

        ids[key] = spaceId;
        keep.Add(key);
        Written(key);
      }

      // ── Levels: the zoom ranges the demo's names are drawn at ──────────────────────────────
      foreach (var level in world.Levels)
      {
        var key = $"level/{level.Name}";
        var id = IdOf(key);
        await _levels.UpdateAsync([new LevelDTO
        {
          id = id.ToString(),
          zoom_level = level.Name,
          zoom_min = level.Min,
          zoom_max = level.Max,
        }]);
        ids[key] = id;
        keep.Add(key);
        Written(key);
      }

      // ── States and rights: rows of the nodes above, they go with them ──────────────────────
      foreach (var state in world.States)
      {
        await _states.UpdateStates([new ObjectStateDTO
        {
          id = Required(state.NodeKey).ToString(),
          states = [.. state.States],
        }]);
      }

      foreach (var right in world.Rights)
      {
        await _rights.Update([new RightDTO
        {
          object_id = Required(right.NodeKey).ToString(),
          subject = right.Subject,
          action = right.Action,
        }]);
      }

      // ── Whatever the world dropped since last time ─────────────────────────────────────────
      var stale = registered
        .Where(pair => !keep.Contains(pair.Key))
        .ToList();
      await DeleteAsync(stale, ct);
      report.Removed = stale.Count;

      await SaveRegistryAsync(world.Key, world.Version, ids.Where(pair => keep.Contains(pair.Key)), ct);

      if (tx is not null) await tx.CommitAsync(ct);
      return report;
    }

    /// Removes everything the registry says the world owns, and the registry itself. A node the
    /// administrator added under a demo space is deleted with that space, as it would be by hand —
    /// the demo deletes its own subtree, not the user's separate nodes.
    public async Task<DemoReport> RemoveAsync(string key, CancellationToken ct)
    {
      await using var tx = await BeginAsync(ct);
      await LockAsync(ct);

      var registered = await LoadRegistryAsync(key, ct);
      var report = new DemoReport { Key = key };

      await DeleteAsync(registered.ToList(), ct);
      report.Removed = registered.Count;

      // The registry first, the config row after it: the two are related only by a database
      // cascade, which EF does not know about when it orders its deletes.
      var rows = await _db.DemoObjects.Where(o => o.demo_key == key).ToListAsync(ct);
      _db.DemoObjects.RemoveRange(rows);
      await _db.SaveChangesAsync(ct);

      var config = await _db.DemoConfigs.FirstOrDefaultAsync(c => c.demo_key == key, ct);
      if (config is not null)
      {
        _db.DemoConfigs.Remove(config);
        await _db.SaveChangesAsync(ct);
      }
      if (tx is not null) await tx.CommitAsync(ct);
      return report;
    }

    // ── Deletion ────────────────────────────────────────────────────────────────────────────

    private async Task DeleteAsync(IReadOnlyList<KeyValuePair<string, Guid>> entries, CancellationToken ct)
    {
      // The order is the foreign-key order: nodes of a space before the space, a type before the
      // picture it points at.
      foreach (var kind in DeleteOrder)
      {
        var ofKind = entries.Where(pair => KindOf(pair.Key) == kind).ToList();
        if (ofKind.Count == 0) continue;

        switch (kind)
        {
          case "object":
            // A value has no foreign key of its own in the model the tests use, and the demo's
            // feeder writes them: take them away before the node they belong to.
            var objectGuids = ofKind.Select(pair => pair.Value).ToList();
            var values = await _db.Values.Where(value => objectGuids.Contains(value.owner_id)).ToListAsync(ct);
            if (values.Count > 0)
            {
              _db.Values.RemoveRange(values);
              await _db.SaveChangesAsync(ct);
            }

            await _updates.Delete(ofKind.Select(pair => pair.Value.ToString()).ToList());
            break;

          case "camera":
            foreach (var pair in ofKind)
            {
              var cameraId = pair.Key["camera/".Length..];
              await _spatial.RemovePlacementAsync(cameraId, pair.Value, ct);
            }
            break;

          case "space":
            foreach (var pair in ofKind)
              await _spatial.RemoveSpaceAsync(pair.Value, ct);
            break;

          case "type":
            await _types.DeleteAsync(ofKind.Select(pair => pair.Value.ToString()).ToList());
            break;

          case "level":
            await _levels.RemoveAsync(ofKind.Select(pair => pair.Value.ToString()).ToList());
            break;

          case "file":
            foreach (var pair in ofKind)
            {
              var file = await _db.Files.FirstOrDefaultAsync(f => f.id == pair.Value, ct);
              if (file is not null) _db.Files.Remove(file);
            }
            await _db.SaveChangesAsync(ct);
            break;
        }
      }
    }

    private static string KindOf(string logicalKey)
    {
      var slash = logicalKey.IndexOf('/');
      return slash < 0 ? logicalKey : logicalKey[..slash];
    }

    // ── Registry ────────────────────────────────────────────────────────────────────────────

    private async Task<Dictionary<string, Guid>> LoadRegistryAsync(string key, CancellationToken ct)
    {
      var rows = await _db.DemoObjects.Where(o => o.demo_key == key).ToListAsync(ct);
      return rows.ToDictionary(o => o.logical_key, o => o.object_id, StringComparer.Ordinal);
    }

    private async Task SaveRegistryAsync(
      string key, int version, IEnumerable<KeyValuePair<string, Guid>> entries, CancellationToken ct)
    {
      var wanted = entries.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
      var rows = await _db.DemoObjects.Where(o => o.demo_key == key).ToListAsync(ct);

      foreach (var row in rows)
      {
        if (wanted.TryGetValue(row.logical_key, out var id)) row.object_id = id;
        else _db.DemoObjects.Remove(row);
      }

      var known = rows.Select(row => row.logical_key).ToHashSet(StringComparer.Ordinal);
      foreach (var (logicalKey, id) in wanted.Where(pair => !known.Contains(pair.Key)))
      {
        _db.DemoObjects.Add(new DBDemoObject
        {
          demo_key = key,
          logical_key = logicalKey,
          object_id = id,
        });
      }

      var config = await _db.DemoConfigs.FirstOrDefaultAsync(c => c.demo_key == key, ct);
      if (config is null)
      {
        config = new DBDemoConfig { demo_key = key };
        _db.DemoConfigs.Add(config);
      }

      config.version = version;
      config.applied_at = DateTime.UtcNow;
      await _db.SaveChangesAsync(ct);
    }

    // ── Helpers ────────────────────────────────────────────────────────────────────────────

    private async Task<IDbContextTransaction?> BeginAsync(CancellationToken ct) =>
      _db.Database.IsRelational()
        ? await _db.Database.BeginTransactionAsync(ct)
        : null;

    /// Two replicas starting with the demo enabled wait for each other here; the lock is released
    /// with the transaction.
    private async Task LockAsync(CancellationToken ct)
    {
      if (!_db.Database.IsRelational()) return;
      await _db.Database.ExecuteSqlRawAsync(
        "SELECT pg_advisory_xact_lock({0})", [LockKey], ct);
    }

    /// Parents before their children, so a child always finds its parent written. A parent outside
    /// the set (a node's parent can be a space) counts as written already.
    private static List<T> ParentsFirst<T>(
      IReadOnlyList<T> items, Func<T, string> key, Func<T, string?> parent)
    {
      var byKey = items.ToDictionary(key, StringComparer.Ordinal);
      var done = new HashSet<string>(StringComparer.Ordinal);
      var result = new List<T>(items.Count);

      void Visit(T item)
      {
        var name = key(item);
        if (done.Contains(name)) return;
        if (done.Add(name) && parent(item) is { } parentKey && byKey.TryGetValue(parentKey, out var ancestor))
          Visit(ancestor);
        result.Add(item);
      }

      foreach (var item in items) Visit(item);
      return result;
    }

    private static Coordinate ToCoordinate(DemoPoint point) => new() { Lat = point.Lat, Lon = point.Lon };

    private static DiagramCoordDTO ToCoord(DemoRect rect) => new()
    {
      left = rect.Left,
      top = rect.Top,
      width = rect.Width,
      height = rect.Height,
      rotation = rect.Rotation,
    };
  }
}
