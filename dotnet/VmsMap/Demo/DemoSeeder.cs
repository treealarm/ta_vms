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
using VmsMap.Domain.Arming;

namespace VmsMap.Demo
{
  /// Applies a `DemoWorld` to the map database and takes it away again. Everything it writes is
  /// recorded in `demo_object` under the world's logical key, so a second install updates the same
  /// rows and a removal deletes only those — a user's own node next to the world is never named by
  /// it and never touched.
  ///
  /// The whole install is one transaction under an advisory lock: two replicas starting with
  /// `DEMO_CONFIG` set must not each create half a world. It is also a writer of the map's object,
  /// state and alarm rows, so it takes the map's locks up front, in the order every other writer
  /// uses (see `LockAsync`).
  public sealed class DemoSeeder
  {
    // hashed into the lock by PostgreSQL; any stable number does, as long as it is ours alone.

    private static readonly string[] DeleteOrder =
      ["object", "camera", "landmark", "space", "type", "level", "file"];

    // The picture a world draws its placed cameras with, when it brings one: pixels of the glyph on
    // the map, and the file key it travels under.
    private const string CameraIconKey = "file/camera-icon";
    private const int CameraIconSize = 34;

    private readonly VmsMapDbContext _db;
    private readonly IMapUpdateService _updates;
    private readonly IDiagramServiceInternal _diagrams;
    private readonly IDiagramTypeService _types;
    private readonly IStatesUpdateService _states;
    private readonly IRightServiceInternal _rights;
    private readonly IGeoService _geo;
    private readonly FileService _files;
    private readonly VisualService _visuals;
    private readonly SpatialDb _spatial;
    private readonly ICameraCatalog _cameras;
    private readonly IDeviceNodeService _deviceNodes;
    private readonly DeviceObjectSettingsStore _deviceSettings;
    private readonly IDeviceDiagramService _deviceDiagrams;
    // Only to remove the layers an older world wrote: the dictionary is the map's own now.
    private readonly ILevelService _levels;

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
      ICameraCatalog cameras,
      IDeviceNodeService deviceNodes,
      DeviceObjectSettingsStore deviceSettings,
      IDeviceDiagramService deviceDiagrams)
    {
      _db = db;
      _updates = updates;
      _diagrams = diagrams;
      _types = types;
      _states = states;
      _rights = rights;
      _geo = geo;
      _files = files;
      _visuals = visuals;
      _spatial = spatial;
      _cameras = cameras;
      _deviceNodes = deviceNodes;
      _deviceSettings = deviceSettings;
      _deviceDiagrams = deviceDiagrams;
      _levels = levels;
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
      var report = new DemoReport { Key = world.Key, Version = world.Version };

      void Skip(string cameraKey)
      {
        if (!report.SkippedCameras.Contains(cameraKey)) report.SkippedCameras.Add(cameraKey);
      }

      // ── Parts a replica stands on: their canonical nodes come first ─────────────────────────
      // A replica's owner is the part's own node, and the device-node service creates it in a
      // transaction of its own — which a connection already in one refuses. So it is asked before
      // the world's transaction opens. A part whose camera this deployment does not have is
      // reported and skipped with its picture; the next install picks it up.
      var replicaOwners = new Dictionary<string, string>(StringComparer.Ordinal);
      foreach (var replica in world.PartReplicas)
      {
        var cameraId = DemoIds.Camera(replica.CameraKey);
        if (await _cameras.GetAsync(cameraId, ct) is null)
        {
          Skip(replica.CameraKey);
          continue;
        }

        var partId = await _deviceNodes.EnsureAsync(
          DeviceObjectId.CameraSource, cameraId, replica.PartKind, replica.Token, ct);
        if (partId is null) Skip(replica.CameraKey);
        else replicaOwners[replica.Key] = partId;
      }

      await using var tx = await BeginAsync(ct);
      await LockAsync(ct);

      var registered = await LoadRegistryAsync(world.Key, ct);
      var ids = new Dictionary<string, Guid>(registered, StringComparer.Ordinal);
      var keep = new HashSet<string>(StringComparer.Ordinal);

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
          source_kind = type.SourceKind,
          device_manufacturer = type.DeviceManufacturer,
          device_model = type.DeviceModel,
          auto_attach = type.AutoAttach,
          regions = type.Regions.Select(region => new DiagramTypeRegionDTO
          {
            region_key = region.Key,
            geometry = ToCoord(region.Geometry),
            styles = region.Styles?.ToDictionary(pair => pair.Key, pair => pair.Value),
            part = region.Part is null
              ? null
              : new DiagramTypePartDTO
              {
                part_kind = region.Part.PartKind,
                match_by = region.Part.MatchBy,
                match_value = region.Part.MatchValue,
              },
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
      var placedCameras = new HashSet<string>(StringComparer.Ordinal);
      foreach (var placement in world.Placements)
      {
        var key = $"camera/{placement.CameraId}";
        var cameraId = DemoIds.Camera(placement.CameraId);
        var spaceId = Required(placement.SpaceKey);
        var camera = await _cameras.GetAsync(cameraId, ct);

        if (camera is null)
        {
          // Kept in the registry if it was there: whatever placement the camera still has was put
          // there by an earlier install, and a removal must find it.
          if (registered.TryGetValue(key, out var earlier)) ids[key] = earlier;
          Skip(placement.CameraId);
          continue;
        }

        // The world moved this camera to another space since the last install: the picture it left
        // there is the world's too, and keeping it would draw the camera twice.
        if (registered.TryGetValue(key, out var earlierSpace) && earlierSpace != spaceId)
          await _spatial.RemovePlacementAsync(cameraId, earlierSpace, ct);

        await _spatial.UpsertPlacementAsync(new CameraPlacement
        {
          CameraId = cameraId,
          SpaceId = spaceId.ToString(),
          IsReference = placement.IsReference,
          Lat = placement.Lat,
          Lon = placement.Lon,
          HeightM = placement.HeightM,
          HeadingDeg = placement.HeadingDeg,
          HfovDeg = placement.HfovDeg,
          RangeM = placement.RangeM,
        }, ct);

        // The picture the plan draws the camera with, rather than the map's own dot. It hangs on
        // the placement's replica node, which is the node drawn there — the camera's canonical node
        // under its server carries no figure. The map turns the picture by the placement's heading.
        //
        // The picture is the world's own row, so its name is the world's to keep in step: a camera
        // renamed between versions of the world — `OnvifSim cam1` became `cam1` — renames its
        // picture, which the placement write does not do for a picture that already exists.
        var canonical = Utils.ConvertObjectIdToGuid(CameraObjectId.For(cameraId));
        var picture = await _db.Markers.AsNoTracking()
          .Where(m => m.parent_id == spaceId && m.owner_id == canonical)
          .Select(m => (Guid?)m.id)
          .FirstOrDefaultAsync(ct);
        if (picture is { } pictureId)
        {
          var siblings = await _db.Markers.AsNoTracking()
            .Where(m => m.owner_id == canonical && m.id != pictureId)
            .Select(m => m.name)
            .ToListAsync(ct);
          var name = ReplicaNames.Next(camera.Name, siblings);
          var marker = await _db.Markers.AsNoTracking().SingleAsync(m => m.id == pictureId, ct);
          if (marker.name != name)
          {
            await _updates.UpdateObjects([new BaseMarkerDTO
            {
              id = Utils.ConvertGuidToObjectId(pictureId),
              parent_id = spaceId.ToString(),
              owner_id = canonical?.ToString(),
              name = name,
            }]);
          }

          if (world.Files.FirstOrDefault(file => file.Key == CameraIconKey) is { } glyph)
          {
            await _visuals.UpdateListAsync([new VisualDTO
            {
              object_id = Utils.ConvertGuidToObjectId(pictureId),
              image = glyph.Content,
              image_width = CameraIconSize,
              image_height = CameraIconSize,
            }]);
          }
        }

        ids[key] = spaceId;
        keep.Add(key);
        placedCameras.Add(placement.CameraId);
        Written(key);
      }

      // ── A part's picture: an ordinary node whose owner is the part ──────────────────────────
      // Written the way an administrator places one from the palette — the node, its point and its
      // look — so a replica made by the world and one made by hand are the same row shape. It
      // carries no camera placement and no state of its own: acting on the picture acts on the part.
      foreach (var replica in world.PartReplicas)
      {
        if (!replicaOwners.TryGetValue(replica.Key, out var ownerId)) continue;

        var id = IdOf(replica.Key);
        var parentId = Required(replica.SpaceKey).ToString();

        await _updates.UpdateObjects([
          new BaseMarkerDTO
          {
            id = id.ToString(),
            parent_id = parentId,
            owner_id = ownerId,
            name = replica.Name,
          }]);

        await _geo.CreateGeo([new FigureGeoDTO
        {
          id = id.ToString(),
          parent_id = parentId,
          name = replica.Name,
          geometry = new GeometryCircleDTO(new Geo2DCoordDTO { Lat = replica.Lat, Lon = replica.Lon }),
          zoom_level = replica.ZoomLevel,
        }]);

        await _visuals.UpdateListAsync([new VisualDTO
        {
          object_id = id.ToString(),
          color = replica.Color,
          text = replica.Name,
        }]);

        ids[replica.Key] = id;
        keep.Add(replica.Key);
        Written(replica.Key);
      }

      // ── A device's diagram: under the camera's picture, made by the service that owns it ─────
      foreach (var diagram in world.DeviceDiagrams)
      {
        if (!placedCameras.Contains(diagram.CameraKey))
        {
          // The device is not in this deployment: the diagram would draw nothing and say nothing.
          Skip(diagram.CameraKey);
          continue;
        }

        // No canvas named: the diagram hangs under the camera's picture and is not drawn on it. The
        // world does not want a second card beside the camera — the map offers a link into it when
        // the camera is selected — and drops the picture an earlier world put there.
        var diagramId = await _deviceDiagrams.AttachAsync(
          DeviceObjectId.CameraSource, DemoIds.Camera(diagram.CameraKey),
          Required(diagram.TypeKey).ToString(), IDeviceDiagramService.DefaultSlot, site: null, ct);
        if (diagramId is null)
          continue;

        await _geo.RemoveAsync([diagramId]);

        // The service names a device's diagram `<camera> / <type>` when it makes the node and keeps
        // whatever name it finds afterwards — a template change is not a rename. The camera is the
        // world's to name, though, and a camera this world renamed (`OnvifSim cam1` became `cam1`)
        // would leave yesterday's name on the diagram hanging under its picture. Only the part
        // before the service's separator is the camera's; the rest is the type's and stays.
        await RenameDeviceDiagramAsync(diagramId, diagram.CameraKey, ct);

        // The thumbnail the map draws when it offers a link into the diagram: the paper it opens on
        // is the same picture.
        var picture = world.Files.Single(file => file.Key == diagram.VisualKey);
        await _visuals.UpdateListAsync([new VisualDTO
        {
          object_id = diagramId,
          image = picture.Content,
          image_width = diagram.VisualWidth,
          image_height = diagram.VisualHeight,
        }]);

        // Registered like every other object of the world, so a removal takes it: the node's delete
        // is what also takes its replicas and the service's row for it.
        ids[diagram.Key] = Guid.Parse(diagramId);
        keep.Add(diagram.Key);
        Written(diagram.Key);
      }

      // ── Landmarks: the points the cameras are calibrated against ───────────────────────────
      foreach (var landmark in world.Landmarks)
      {
        // A landmark gets its id from the server on the first install and keeps it afterwards, as
        // a space does: observations and marks recorded against it name the id, not the key.
        var id = registered.TryGetValue(landmark.Key, out var known) ? known.ToString() : string.Empty;
        var saved = await _spatial.UpsertLandmarkAsync(new Landmark
        {
          Id = id,
          SpaceId = Required(landmark.SpaceKey).ToString(),
          Name = landmark.Name,
          Lat = landmark.Lat,
          Lon = landmark.Lon,
          HeightM = landmark.HeightM,
        }, ct);

        ids[landmark.Key] = Guid.Parse(saved.Id);
        keep.Add(landmark.Key);
        Written(landmark.Key);
      }

      // ── Calibration: what each camera sees of those landmarks ─────────────────────────────
      // The marks replace the camera's whole list, so a world that drops one leaves none behind.
      // A camera the deployment lacks is skipped with its placement, and reported there.
      foreach (var group in world.GroundMarks.GroupBy(mark => mark.CameraKey, StringComparer.Ordinal))
      {
        if (!placedCameras.Contains(group.Key))
        {
          // A mark for a camera the world never placed is a camera this install cannot show either.
          Skip(group.Key);
          continue;
        }

        var marks = new GroundObservations { CameraId = DemoIds.Camera(group.Key) };
        marks.Observations.AddRange(group.Select(mark => new GroundObservation
        {
          LandmarkId = Required(mark.LandmarkKey).ToString(),
          X = (float)mark.X,
          Y = (float)mark.Y,
        }));
        await _spatial.SetGroundObservationsAsync(marks, ct);
      }

      foreach (var group in world.PtzObservations.GroupBy(observation => observation.CameraKey, StringComparer.Ordinal))
      {
        if (!placedCameras.Contains(group.Key))
        {
          Skip(group.Key);
          continue;
        }

        var observations = new PtzGeoObservations { CameraId = DemoIds.Camera(group.Key) };
        foreach (var observation in group)
        {
          var row = new PtzGeoObservation
          {
            LandmarkId = Required(observation.LandmarkKey).ToString(),
            Pan = (float)observation.Pan,
            Tilt = (float)observation.Tilt,
          };
          if (observation.Zoom is { } zoom) row.Zoom = (float)zoom;
          observations.Observations.Add(row);
        }
        await _spatial.SetObservationsAsync(observations, ct);
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
      // The cameras their pictures held are read before the pictures go: their flags are derived
      // from the committed world, and by then these rows no longer exist.
      var releasedCameras = await CamerasOfAsync(stale, ct);
      await DeleteAsync(stale, ct);
      report.Removed = stale.Count;

      await SaveRegistryAsync(world.Key, world.Version, ids.Where(pair => keep.Contains(pair.Key)), ct);

      if (tx is not null) await tx.CommitAsync(ct);

      // The world is committed: every camera drawn under it, and every camera whose picture it
      // released, is derived now. The placement writes inside the transaction did not derive —
      // they read an uncommitted tree — so this is the pass that settles their flags.
      var armingCameras = new HashSet<string>(releasedCameras, StringComparer.Ordinal);
      var keptRoots = ids
        .Where(pair => keep.Contains(pair.Key) && KindIsContainer(pair.Key))
        .Select(pair => pair.Value.ToString())
        .ToList();
      armingCameras.UnionWith(await _spatial.CamerasUnderAsync(keptRoots, ct));

      // ── A device's behaviour: the map's own, so the world installs it here ───────────────────
      // The part's name is VmsCfg's; who decides its arming and what its contact means is the
      // map's, and a copy anywhere else would be the second source this release removed.
      //
      // After the world's transaction, not inside it: ensuring a part's node is the node service's
      // work and it opens a transaction of its own, which a connection already in one refuses. (The
      // nodes a replica stands on were ensured before the transaction for the same reason.) The
      // world is complete without these rows — a part with none keeps the defaults — so a failure
      // here costs the demo's arming until the next install, not half a world.
      try
      {
        foreach (var device in world.Devices)
        {
          if (!placedCameras.Contains(device.CameraKey))
          {
            Skip(device.CameraKey);
            continue;
          }

          var partId = await _deviceNodes.EnsureAsync(
            DeviceObjectId.CameraSource, DemoIds.Camera(device.CameraKey),
            device.PartKind, device.Token, ct);
          if (partId is null)
          {
            Skip(device.CameraKey);
            continue;
          }

          await _deviceSettings.SetAsync(
            partId, device.ArmMode, device.TriggerOn, device.AlarmEnabled, ct);
        }
      }
      finally
      {
        // After the settings, so a world that writes a camera's own mode is derived with it; in
        // `finally`, so a failure there does not leave the committed world's flags underived.
        // `ReconcileManyAsync` survives one camera's failure and logs it.
        await _spatial.ReconcileCamerasAsync(armingCameras, ct);
      }

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

      // Read before the pictures go: a camera whose picture the removal takes away is off guard
      // once the world is gone, and its flag is derived from the committed state after the commit.
      var entries = registered.ToList();
      var releasedCameras = await CamerasOfAsync(entries, ct);

      await DeleteAsync(entries, ct);
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

      // The removal's placement writes did not derive inside the transaction; the flags of the
      // cameras it took away are settled here, from the committed state.
      await _spatial.ReconcileCamerasAsync(releasedCameras, ct);
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
            // A value has no foreign key of its own in the model the tests use: take the values
            // away before the nodes they belong to. A replica owns nothing — the readings panel
            // the world shipped up to version 19 wrote its values into the camera's canonical
            // node — so its owner's keys are named too, under the names that panel used.
            var objectGuids = ofKind.Select(pair => pair.Value).ToList();
            var owners = await _db.Markers.AsNoTracking()
              .Where(m => objectGuids.Contains(m.id) && m.owner_id != null)
              .Select(m => m.owner_id!.Value)
              .ToListAsync(ct);
            string[] retiredReadings = ["lat", "lon", "speed", "relay", "sensor"];
            var retiredValueIds = owners
              .SelectMany(owner => retiredReadings.Select(name => DemoIds.For($"value/{owner}/{name}")))
              .ToList();
            var values = await _db.Values
              .Where(value => objectGuids.Contains(value.owner_id) || retiredValueIds.Contains(value.id))
              .ToListAsync(ct);
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
              // The registry key holds the world's name for the camera; the map speaks ids.
              var cameraName = pair.Key["camera/".Length..];
              await _spatial.RemovePlacementAsync(DemoIds.Camera(cameraName), pair.Value, ct);
            }
            break;

          case "landmark":
            // A landmark takes its observations with it, so the calibration goes without naming it.
            foreach (var pair in ofKind)
              await _spatial.RemoveLandmarkAsync(pair.Value, ct);
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

    /// <summary>A space or a node: a registry entry whose subtree may hold camera pictures.</summary>
    private static bool KindIsContainer(string logicalKey)
    {
      var kind = KindOf(logicalKey);
      return string.Equals(kind, "space", StringComparison.Ordinal)
          || string.Equals(kind, "object", StringComparison.Ordinal);
    }

    /// <summary>
    /// The cameras a set of registry entries stands for: a camera key names one, a space or an
    /// object key names whatever is drawn under it. Called before those entries are deleted, so
    /// the pictures are still there to read.
    /// </summary>
    private async Task<HashSet<string>> CamerasOfAsync(
      IReadOnlyCollection<KeyValuePair<string, Guid>> entries, CancellationToken ct)
    {
      var cameras = entries
        .Where(pair => string.Equals(KindOf(pair.Key), "camera", StringComparison.Ordinal))
        .Select(pair => DemoIds.Camera(pair.Key["camera/".Length..]))
        .ToHashSet(StringComparer.Ordinal);

      var roots = entries
        .Where(pair => KindIsContainer(pair.Key))
        .Select(pair => pair.Value.ToString())
        .ToList();
      if (roots.Count > 0)
        cameras.UnionWith(await _spatial.CamerasUnderAsync(roots, ct));

      return cameras;
    }

    /// The camera's name at the head of its diagram's name, as `DeviceDiagramService.CreateAsync`
    /// writes it. The separator is that service's; this only reads what it wrote.
    private const string DiagramNameSeparator = " / ";

    private async Task RenameDeviceDiagramAsync(string diagramId, string cameraKey, CancellationToken ct)
    {
      if (!Guid.TryParse(diagramId, out var id)) return;
      var marker = await _db.Markers.AsNoTracking().FirstOrDefaultAsync(m => m.id == id, ct);
      var camera = await _cameras.GetAsync(DemoIds.Camera(cameraKey), ct);
      if (marker is null || camera?.Name is not { Length: > 0 } cameraName) return;

      var separator = marker.name.IndexOf(DiagramNameSeparator, StringComparison.Ordinal);
      if (separator <= 0 || marker.name.StartsWith(cameraName + DiagramNameSeparator, StringComparison.Ordinal))
        return;

      // The whole row, not just the name: a hierarchy write takes the parent and the owner it is
      // given, so leaving them out would orphan the node it is renaming.
      await _updates.UpdateObjects([
        new BaseMarkerDTO
        {
          id = diagramId,
          parent_id = marker.parent_id is { } parent ? Utils.ConvertGuidToObjectId(parent) : null,
          owner_id = marker.owner_id is { } owner ? Utils.ConvertGuidToObjectId(owner) : null,
          name = cameraName + marker.name[separator..],
        },
      ]);
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
    /// <para>
    /// The map's own locks are taken in the same call, before this transaction writes its first
    /// row, and not at the first state write further down. The install writes the same object,
    /// state and alarm rows as the device path, which takes these locks before its writes; taking
    /// them late — once this long transaction already holds row locks — is the reverse order and
    /// deadlocked a bench install against the connectivity poller.
    /// </para>
    private async Task LockAsync(CancellationToken ct)
    {
      await MapLocks.AcquireDemoInstallAsync(_db, ct);
      await MapLocks.AcquireAllAsync(_db, ct);
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
