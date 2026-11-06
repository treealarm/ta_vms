using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Vms.Map;
using VmsMap.Clients;
using VmsMap.Domain;
using VmsMap.PubSub;
using NtsPoint = NetTopologySuite.Geometries.Point;
using NtsPolygon = NetTopologySuite.Geometries.Polygon;

namespace VmsMap.Db
{
  /// A request that cannot be stored as asked: a missing parent, a value out of range, a loop in
  /// the tree. The message is meant for the administrator who sent it.
  public sealed class SpatialConfigException(string message) : Exception(message);

  /// Spaces, plan images, camera placements, landmarks and PTZ and ground observations. A space,
  /// a placement and a landmark are nodes of the one tree with side rows of their own; a camera is
  /// one node whose id is derived from the camera id, and its other placements are replicas of it.
  /// See Grpc/migrations/0002_spaces.sql for what each table means.
  public class SpatialDb : ICameraGeoWriter
  {
    private readonly VmsMapDbContext _db;    private readonly IMapService _mapService;
    private readonly IMapUpdateService _mapUpdateService;
    private readonly IGeoService _geoService;
    private readonly ICameraCatalog _cameraCatalog;
    private readonly IMapConfigNotifier _notifier;

    public SpatialDb(
      VmsMapDbContext db,
      IMapService mapService,
      IMapUpdateService mapUpdateService,
      IGeoService geoService,
      ICameraCatalog cameraCatalog,
      IMapConfigNotifier notifier)
    {
      _db = db ?? throw new ArgumentNullException(nameof(db));
      _mapService = mapService ?? throw new ArgumentNullException(nameof(mapService));
      _mapUpdateService = mapUpdateService ?? throw new ArgumentNullException(nameof(mapUpdateService));
      _geoService = geoService ?? throw new ArgumentNullException(nameof(geoService));
      _cameraCatalog = cameraCatalog ?? throw new ArgumentNullException(nameof(cameraCatalog));
      _notifier = notifier ?? throw new ArgumentNullException(nameof(notifier));
    }

    // ── Spaces ────────────────────────────────────────────────────────────────

    public async Task<ListSpacesResponse> ListSpacesAsync(CancellationToken ct)
    {
      var rows = await (from s in _db.Spaces.AsNoTracking()
                        join o in _db.Markers.AsNoTracking() on s.object_id equals o.id
                        orderby s.ordinal, o.name
                        select new { s, o }).ToListAsync(ct);
      var plans = await LoadPlansAsync(null, ct);
      var footprints = await LoadFootprintsAsync(rows.Select(r => r.s.object_id).ToList(), ct);

      var response = new ListSpacesResponse();
      response.Spaces.AddRange(rows.Select(r => ToProto(
        r.s, r.o, plans.GetValueOrDefault(r.s.object_id), footprints.GetValueOrDefault(r.s.object_id))));
      return response;
    }

    public async Task<Space?> GetSpaceAsync(Guid spaceId, CancellationToken ct)
    {
      var row = await _db.Spaces.AsNoTracking().FirstOrDefaultAsync(s => s.object_id == spaceId, ct);
      if (row is null)
        return null;
      var obj = await _db.Markers.AsNoTracking().FirstOrDefaultAsync(m => m.id == spaceId, ct);
      if (obj is null)
        return null;

      var plans = await LoadPlansAsync(spaceId, ct);
      var footprints = await LoadFootprintsAsync([spaceId], ct);
      return ToProto(row, obj, plans.GetValueOrDefault(spaceId), footprints.GetValueOrDefault(spaceId));
    }

    /// Creates the space when it has no id yet; otherwise updates it. A space keeps its kind: a map
    /// turned into a plan would have nothing to draw, and a plan turned into a map would lose its
    /// picture.
    public async Task<Space> UpsertSpaceAsync(Space space, CancellationToken ct)
    {
      if (string.IsNullOrWhiteSpace(space.Name))
        throw new SpatialConfigException("A space needs a name");
      if (!double.IsFinite(space.ElevationM))
        throw new SpatialConfigException("Elevation must be a number");
      RequireView(space.View);
      var parentId = string.IsNullOrEmpty(space.ParentId) ? (Guid?)null : ParseId(space.ParentId, "parent space");
      var kind = space.Kind == SpaceKind.Map ? SpaceKinds.Map : SpaceKinds.Plan;
      if (kind == SpaceKinds.Map && parentId is not null)
        throw new SpatialConfigException("A map is always a root; a space inside another one is a plan");
      var footprint = ReadFootprint(space.Footprint, parentId);

      await using var tx = await BeginTransactionAsync(ct);

      DBSpace? row = null;
      if (!string.IsNullOrEmpty(space.Id))
      {
        var id = ParseId(space.Id, "space");
        row = await _db.Spaces.FirstOrDefaultAsync(s => s.object_id == id, ct)
          ?? throw new SpatialConfigException($"Space '{space.Id}' does not exist");
        if (row.kind != kind)
          throw new SpatialConfigException("A space cannot change between map and plan");
      }

      if (parentId is not null)
      {
        // Spaces number in the dozens: walking the tree in memory makes the loop check cheap to
        // get right.
        var parents = await (from s in _db.Spaces.AsNoTracking()
                             join o in _db.Markers.AsNoTracking() on s.object_id equals o.id
                             select new { s.object_id, o.parent_id })
                            .ToDictionaryAsync(x => x.object_id, x => x.parent_id, ct);
        if (!parents.ContainsKey(parentId.Value))
          throw new SpatialConfigException($"Space '{space.ParentId}' does not exist");
        if (row is not null && IsSelfOrDescendant(parents, parentId.Value, row.object_id))
          throw new SpatialConfigException("A space cannot be put inside itself");
      }

      var name = space.Name.Trim();
      var siblings = from s in _db.Spaces
                     join o in _db.Markers on s.object_id equals o.id
                     where o.name == name
                     select new { s.object_id, o.parent_id };
      siblings = parentId is null
        ? siblings.Where(x => x.parent_id == null)
        : siblings.Where(x => x.parent_id == parentId.Value);
      if (row is not null)
        siblings = siblings.Where(x => x.object_id != row.object_id);
      if (await siblings.AnyAsync(ct))
        throw new SpatialConfigException($"There already is a space named '{name}' here");

      var objectId = row?.object_id ?? Guid.NewGuid();
      await _mapService.UpdateHierarchyAsync([
        new BaseMarkerDTO { id = objectId.ToString(), parent_id = parentId?.ToString(), name = name }]);

      if (row is null)
      {
        row = new DBSpace { object_id = objectId, kind = kind };
        _db.Spaces.Add(row);
      }

      row.kind = kind;
      // A map is real by definition: its background is the real world.
      row.real_coordinates = kind == SpaceKinds.Map || space.RealCoordinates;
      row.ordinal = space.Ordinal;
      row.elevation_m = space.ElevationM;
      row.view_south = space.View.SouthWest.Lat;
      row.view_west = space.View.SouthWest.Lon;
      row.view_north = space.View.NorthEast.Lat;
      row.view_east = space.View.NorthEast.Lon;
      await _db.SaveChangesAsync(ct);

      if (footprint is not null)
        await _geoService.CreateGeo([PolygonFigure(objectId, footprint)]);
      else
        await _geoService.RemoveAsync([objectId.ToString()]);
      await _db.SaveChangesAsync(ct);

      if (tx is not null)
        await tx.CommitAsync(ct);

      await NotifyAsync([objectId.ToString()], [], ct);
      return await GetSpaceAsync(objectId, ct)
        ?? throw new SpatialConfigException($"Space '{objectId}' does not exist");
    }

    /// Removes the space with everything nested in it. A camera whose reference placement is in
    /// the subtree does not disappear with it: its node is taken out of the subtree first, so
    /// pictures of it in other spaces keep a node to show.
    public async Task<bool> RemoveSpaceAsync(Guid spaceId, CancellationToken ct)
    {
      if (!await _db.Spaces.AnyAsync(s => s.object_id == spaceId, ct))
        return false;

      await using var tx = await BeginTransactionAsync(ct);

      var subtree = await _mapService.GetSubtreeIdsAsync([spaceId.ToString()]);
      var subtreeIds = subtree
        .Select(Utils.ConvertObjectIdToGuid)
        .Where(id => id.HasValue)
        .Select(id => id!.Value)
        .ToList();

      var cameras = new HashSet<string>(StringComparer.Ordinal);

      // The placement rows in the subtree belong to replicas of cameras, not to the cameras'
      // canonical nodes -- those stand under their server, outside any space. A camera whose
      // reference was here loses its position and stays in the tree; the rows go with their nodes.
      var placements = await _db.CameraPlacements
        .Where(p => subtreeIds.Contains(p.object_id))
        .ToListAsync(ct);
      cameras.UnionWith(placements.Select(p => p.camera_id));
      _db.CameraPlacements.RemoveRange(placements);
      _db.GeoObjects.RemoveRange(
        await _db.GeoObjects.Where(g => subtreeIds.Contains(g.id)).ToListAsync(ct));

      // Landmarks go with the subtree, and the observations made on them with the landmarks.
      var landmarks = await _db.Landmarks
        .Where(l => subtreeIds.Contains(l.object_id))
        .Select(l => l.object_id)
        .ToListAsync(ct);
      if (landmarks.Count > 0)
      {
        cameras.UnionWith(await _db.PtzGeoObservations
          .Where(o => landmarks.Contains(o.landmark_id))
          .Select(o => o.camera_id).ToListAsync(ct));
        cameras.UnionWith(await _db.GroundObservations
          .Where(o => landmarks.Contains(o.landmark_id))
          .Select(o => o.camera_id).ToListAsync(ct));
      }

      await _db.SaveChangesAsync(ct);

      var removed = await _mapUpdateService.Delete([spaceId.ToString()]);

      if (tx is not null)
        await tx.CommitAsync(ct);

      await NotifyAsync([spaceId.ToString()], cameras, ct);
      return removed > 0;
    }

    // ── Plan images ───────────────────────────────────────────────────────────

    public async Task<Space> SavePlanAsync(SpacePlanUploadHeader header, byte[] image, CancellationToken ct)
    {
      var spaceId = ParseId(header.SpaceId, "space");
      RequireCorners(header.Corners);
      if (header.WidthPx == 0 || header.HeightPx == 0)
        throw new SpatialConfigException("The image size must be given");

      await using var tx = await BeginTransactionAsync(ct);

      var space = await _db.Spaces.AsNoTracking().FirstOrDefaultAsync(s => s.object_id == spaceId, ct)
        ?? throw new SpatialConfigException($"Space '{header.SpaceId}' does not exist");
      if (space.kind != SpaceKinds.Plan)
        throw new SpatialConfigException("A map is drawn on map tiles; only a plan has a picture");

      var row = await _db.SpacePlans.FirstOrDefaultAsync(p => p.space_id == spaceId, ct);
      if (row is null)
      {
        row = new DBSpacePlan { space_id = spaceId };
        _db.SpacePlans.Add(row);
      }

      row.mime = header.Mime;
      row.width_px = (int)header.WidthPx;
      row.height_px = (int)header.HeightPx;
      SetCorners(row, header.Corners);
      row.etag = Convert.ToHexStringLower(SHA256.HashData(image));
      row.image = image;
      await _db.SaveChangesAsync(ct);

      if (tx is not null)
        await tx.CommitAsync(ct);

      await NotifyAsync([spaceId.ToString()], [], ct);
      return await GetSpaceAsync(spaceId, ct)
        ?? throw new SpatialConfigException($"Space '{spaceId}' does not exist");
    }

    public async Task<Space> SetPlanCornersAsync(Guid spaceId, PlanCorners corners, CancellationToken ct)
    {
      RequireCorners(corners);

      await using var tx = await BeginTransactionAsync(ct);

      _ = await _db.Spaces.AsNoTracking().FirstOrDefaultAsync(s => s.object_id == spaceId, ct)
        ?? throw new SpatialConfigException($"Space '{spaceId}' does not exist");

      var row = await _db.SpacePlans.FirstOrDefaultAsync(p => p.space_id == spaceId, ct)
        ?? throw new SpatialConfigException("This space has no plan to align");
      SetCorners(row, corners);
      await _db.SaveChangesAsync(ct);

      if (tx is not null)
        await tx.CommitAsync(ct);

      await NotifyAsync([spaceId.ToString()], [], ct);
      return await GetSpaceAsync(spaceId, ct)
        ?? throw new SpatialConfigException($"Space '{spaceId}' does not exist");
    }

    /// The image with its type and ETag, or null when the space has no plan.
    public async Task<(string Mime, string Etag, byte[] Image)?> GetPlanImageAsync(
      Guid spaceId, IReadOnlyCollection<string> ifNoneMatch, CancellationToken ct)
    {
      var head = await _db.SpacePlans.AsNoTracking()
        .Where(p => p.space_id == spaceId)
        .Select(p => new { p.mime, p.etag })
        .FirstOrDefaultAsync(ct);
      if (head is null)
        return null;

      // The bytes are not read at all when the caller's copy is current.
      if (ifNoneMatch.Contains(head.etag) || ifNoneMatch.Contains("*"))
        return (head.mime, head.etag, []);

      var image = await _db.SpacePlans.AsNoTracking()
        .Where(p => p.space_id == spaceId)
        .Select(p => p.image)
        .FirstAsync(ct);
      return (head.mime, head.etag, image);
    }

    public async Task<bool> RemovePlanAsync(Guid spaceId, CancellationToken ct)
    {
      var row = await _db.SpacePlans.FirstOrDefaultAsync(p => p.space_id == spaceId, ct);
      if (row is null)
        return false;

      _db.SpacePlans.Remove(row);
      await _db.SaveChangesAsync(ct);
      await NotifyAsync([spaceId.ToString()], [], ct);
      return true;
    }

    // ── Camera placements ─────────────────────────────────────────────────────

    public async Task<List<CameraPlacement>> ListPlacementsAsync(CancellationToken ct)
    {
      // The space a placement is in is the canvas its point is drawn on, not the node's parent: a
      // camera may hang under a container on that canvas. See CanvasResolver.
      var rows = await (from p in _db.CameraPlacements.AsNoTracking()
                        join g in _db.GeoObjects.AsNoTracking() on p.object_id equals g.id
                        where g.canvas_id != null
                        select new { p, spaceId = g.canvas_id!.Value }).ToListAsync(ct);
      var points = await LoadPointsAsync(rows.Select(r => r.p.object_id).ToList(), ct);

      var result = new List<CameraPlacement>();
      foreach (var row in rows)
      {
        if (points.TryGetValue(row.p.object_id, out var point))
          result.Add(ToPlacementProto(row.p, row.spaceId, point.Lat, point.Lon));
      }
      return result;
    }

    /// Where the camera really stands and the space that is in; null when it has no reference.
    public async Task<(CameraPlacement Placement, Space Space)?> GetReferencePlacementAsync(
      string cameraId, CancellationToken ct)
    {
      var found = await PlacedReferenceAsync(cameraId, ct);
      if (found is null)
        return null;
      var (placement, space) = found.Value;
      var points = await LoadPointsAsync([placement.object_id], ct);
      if (!points.TryGetValue(placement.object_id, out var point))
        return null;

      var spaceObject = await _db.Markers.AsNoTracking().FirstOrDefaultAsync(m => m.id == space.object_id, ct);
      if (spaceObject is null)
        return null;
      var footprints = await LoadFootprintsAsync([space.object_id], ct);
      return (
        ToPlacementProto(placement, space.object_id, point.Lat, point.Lon),
        ToProto(space, spaceObject, null, footprints.GetValueOrDefault(space.object_id)));
    }

    /// Stores one placement. A placement is always a replica of the camera's canonical node: the
    /// canonical node stands under its server and never moves, and what is drawn on a map, a plan or
    /// a diagram is a node of the camera's own there. A camera's first placement becomes its
    /// reference; making a placement the reference takes the flag from whichever one had it, and
    /// leaves that one as an ordinary picture, its drawing untouched. Clearing the flag leaves the
    /// camera with no reference at all -- another placement is never promoted behind the
    /// administrator's back, since that would quietly change where the camera is aimed from.
    public async Task UpsertPlacementAsync(CameraPlacement placement, CancellationToken ct)
    {
      RequireLatLon(placement.Lat, placement.Lon, "camera");
      if (!double.IsFinite(placement.HeightM))
        throw new SpatialConfigException("Height must be a number");
      if (!(placement.HfovDeg > 0 && placement.HfovDeg <= 360))
        throw new SpatialConfigException("The field of view must be between 0 and 360 degrees");
      if (!(placement.RangeM > 0 && double.IsFinite(placement.RangeM)))
        throw new SpatialConfigException("The range must be positive");
      if (!double.IsFinite(placement.HeadingDeg))
        throw new SpatialConfigException("The heading must be a number");
      if (string.IsNullOrWhiteSpace(placement.CameraId))
        throw new SpatialConfigException("A placement needs a camera");
      var spaceId = ParseId(placement.SpaceId, "space");

      var camera = await _cameraCatalog.GetAsync(placement.CameraId, ct)
        ?? throw new SpatialConfigException($"Camera '{placement.CameraId}' does not exist");

      await using var tx = await BeginTransactionAsync(ct);

      if (!await _db.Spaces.AnyAsync(s => s.object_id == spaceId, ct))
        throw new SpatialConfigException($"Space '{placement.SpaceId}' does not exist");

      var canonicalId = Utils.ConvertObjectIdToGuid(CameraObjectId.For(placement.CameraId))!.Value;
      await EnsureCanonicalNodeAsync(canonicalId, camera, ct);

      var oldReference = await _db.CameraPlacements
        .FirstOrDefaultAsync(p => p.camera_id == placement.CameraId && p.is_reference, ct);
      var target = await RowInSpaceAsync(placement.CameraId, spaceId, ct);

      // A camera's first placement becomes its reference; after that the caller decides.
      var isReference = placement.IsReference
        || (!await _db.CameraPlacements.AnyAsync(p => p.camera_id == placement.CameraId, ct));

      var replicaId = target?.object_id ?? Guid.NewGuid();
      var changed = new HashSet<string>(StringComparer.Ordinal) { replicaId.ToString() };

      if (target is null)
      {
        // A picture of its own on this canvas, under whatever container the administrator gave it;
        // the placement editor creates a node per space, not per camera.
        await _mapService.UpdateHierarchyAsync([new BaseMarkerDTO
        {
          id = replicaId.ToString(),
          parent_id = spaceId.ToString(),
          owner_id = canonicalId.ToString(),
          name = camera.Name,
        }]);

        target = new DBCameraPlacement
        {
          object_id = replicaId,
          camera_id = placement.CameraId,
        };
        _db.CameraPlacements.Add(target);
      }

      if (isReference && oldReference is not null && oldReference.object_id != replicaId)
      {
        // Demoted before the new one takes the flag: the schema allows one reference row per
        // camera, and a single SaveChanges would order the two updates by key.
        oldReference.is_reference = false;
        await _db.SaveChangesAsync(ct);
        changed.Add(oldReference.object_id.ToString());
      }

      target.is_reference = isReference;
      ApplyValues(target, placement);
      // The figure belongs to the replica: the canonical node carries meaning, not a picture.
      await _geoService.CreateGeo([PointFigure(replicaId, placement.Lat, placement.Lon)]);
      await _db.SaveChangesAsync(ct);

      if (tx is not null)
        await tx.CommitAsync(ct);

      await NotifyAsync(changed, [placement.CameraId], ct);
    }

    /// <summary>
    /// Moves a camera's canonical node to a place the device itself reported.
    /// </summary>
    /// <remarks>
    /// Deliberately narrower than <see cref="UpsertPlacementAsync"/>: this is the device talking,
    /// and the only thing it can be trusted to say is where it is. The space, the parent, the
    /// height, the heading and the field of view stay the administrator's, and no picture moves.
    /// A camera with no reference placement is a no-op — inventing one is a decision for a person,
    /// and the next tick will move the point as soon as one exists.
    /// </remarks>
    /// <returns>False when the camera has no reference placement.</returns>
    public async Task<bool> MoveReferencePointAsync(
      string cameraId, double lat, double lon, CancellationToken ct)
    {
      RequireLatLon(lat, lon, "camera");

      var reference = await _db.CameraPlacements.AsNoTracking()
        .FirstOrDefaultAsync(p => p.camera_id == cameraId && p.is_reference, ct);
      if (reference is null)
        return false;

      await using var tx = await BeginTransactionAsync(ct);
      await _geoService.CreateGeo([PointFigure(reference.object_id, lat, lon)]);
      await _db.SaveChangesAsync(ct);
      if (tx is not null)
        await tx.CommitAsync(ct);

      // The camera id travels too: the map's own clients reload placements by it, and the tracking
      // models drop theirs — a model fitted at the old point is a wrong answer at the new one.
      await NotifyAsync([reference.object_id.ToString()], [cameraId], ct);
      return true;
    }

    public async Task<bool> RemovePlacementAsync(string cameraId, Guid spaceId, CancellationToken ct)
    {
      await using var tx = await BeginTransactionAsync(ct);

      var row = await RowInSpaceAsync(cameraId, spaceId, ct);
      if (row is null)
        return false;

      // A placement always belongs to a replica: taking it off the canvas takes the replica with
      // it. The canonical node stays under its server, without a position until another placement
      // makes one of its replicas the reference.
      await DeletePictureAsync(row.object_id, ct);

      if (tx is not null)
        await tx.CommitAsync(ct);

      await NotifyAsync([row.object_id.ToString()], [cameraId], ct);
      return true;
    }

    // ── Landmarks ─────────────────────────────────────────────────────────────

    public async Task<List<Landmark>> ListLandmarksAsync(Guid spaceId, CancellationToken ct)
    {
      var rows = await (from l in _db.Landmarks.AsNoTracking()
                        join o in _db.Markers.AsNoTracking() on l.object_id equals o.id
                        join g in _db.GeoObjects.AsNoTracking() on l.object_id equals g.id
                        where g.canvas_id == spaceId
                        orderby o.name
                        select new { l, o }).ToListAsync(ct);
      var points = await LoadPointsAsync(rows.Select(r => r.l.object_id).ToList(), ct);

      var result = new List<Landmark>();
      foreach (var row in rows)
      {
        if (points.TryGetValue(row.l.object_id, out var point))
          result.Add(ToProto(row.l, spaceId, row.o.name, point.Lat, point.Lon));
      }
      return result;
    }

    public async Task<Landmark> UpsertLandmarkAsync(Landmark landmark, CancellationToken ct)
    {
      if (string.IsNullOrWhiteSpace(landmark.Name))
        throw new SpatialConfigException("A landmark needs a name");
      RequireLatLon(landmark.Lat, landmark.Lon, "landmark");
      if (!double.IsFinite(landmark.HeightM))
        throw new SpatialConfigException("Height must be a number");
      var spaceId = ParseId(landmark.SpaceId, "space");
      var name = landmark.Name.Trim();

      await using var tx = await BeginTransactionAsync(ct);

      if (!await _db.Spaces.AnyAsync(s => s.object_id == spaceId, ct))
        throw new SpatialConfigException($"Space '{landmark.SpaceId}' does not exist");

      DBLandmark row;
      if (string.IsNullOrEmpty(landmark.Id))
      {
        var objectId = Guid.NewGuid();
        await _mapService.UpdateHierarchyAsync([
          new BaseMarkerDTO { id = objectId.ToString(), parent_id = spaceId.ToString(), name = name }]);
        row = new DBLandmark { object_id = objectId, height_m = landmark.HeightM };
        _db.Landmarks.Add(row);
      }
      else
      {
        var id = ParseId(landmark.Id, "landmark");
        row = await _db.Landmarks.FirstOrDefaultAsync(l => l.object_id == id, ct)
          ?? throw new SpatialConfigException($"Landmark '{landmark.Id}' does not exist");

        var marker = await _db.Markers.AsNoTracking().FirstOrDefaultAsync(m => m.id == id, ct);
        // Moved to another space, every observation of it would describe a place its cameras
        // cannot see. The canvas decides, not the parent: the landmark may hang under a container.
        if (marker is null || await CanvasOfAsync(id, ct) != spaceId)
          throw new SpatialConfigException("A landmark cannot move to another space");

        // The name changes, the parent does not: a container keeps its landmark.
        await _mapService.UpdateHierarchyAsync([
          new BaseMarkerDTO { id = id.ToString(), parent_id = marker.parent_id?.ToString(), name = name }]);
        row.height_m = landmark.HeightM;
      }

      await _geoService.CreateGeo([PointFigure(row.object_id, landmark.Lat, landmark.Lon)]);
      await _db.SaveChangesAsync(ct);

      if (tx is not null)
        await tx.CommitAsync(ct);

      var observers = await ObserversOfLandmarkAsync([row.object_id], ct);
      await NotifyAsync([row.object_id.ToString()], observers, ct);
      return ToProto(row, spaceId, name, landmark.Lat, landmark.Lon);
    }

    public async Task<bool> RemoveLandmarkAsync(Guid landmarkId, CancellationToken ct)
    {
      var row = await _db.Landmarks.FirstOrDefaultAsync(l => l.object_id == landmarkId, ct);
      if (row is null)
        return false;

      var observers = await ObserversOfLandmarkAsync([landmarkId], ct);

      await using var tx = await BeginTransactionAsync(ct);

      // The observations on the landmark describe nothing once it is gone; in PostgreSQL the
      // foreign keys cascade, and they are removed here explicitly as well so that the same code
      // runs wherever the context points.
      _db.PtzGeoObservations.RemoveRange(
        await _db.PtzGeoObservations.Where(o => o.landmark_id == landmarkId).ToListAsync(ct));
      _db.GroundObservations.RemoveRange(
        await _db.GroundObservations.Where(o => o.landmark_id == landmarkId).ToListAsync(ct));
      _db.Landmarks.Remove(row);
      _db.GeoObjects.RemoveRange(
        await _db.GeoObjects.Where(g => g.id == landmarkId).ToListAsync(ct));
      _db.Markers.RemoveRange(
        await _db.Markers.Where(m => m.id == landmarkId).ToListAsync(ct));
      await _db.SaveChangesAsync(ct);

      if (tx is not null)
        await tx.CommitAsync(ct);

      await NotifyAsync([landmarkId.ToString()], observers, ct);
      return true;
    }

    // ── PTZ observations ──────────────────────────────────────────────────────

    public async Task<PtzGeoObservations> GetObservationsAsync(string cameraId, CancellationToken ct)
    {
      var rows = await _db.PtzGeoObservations.AsNoTracking()
        .Where(o => o.camera_id == cameraId)
        .ToListAsync(ct);

      var response = new PtzGeoObservations { CameraId = cameraId };
      response.Observations.AddRange(rows.Select(o =>
      {
        var observation = new PtzGeoObservation
        {
          LandmarkId = o.landmark_id.ToString(),
          Pan = o.pan,
          Tilt = o.tilt,
        };
        if (o.zoom is { } zoom)
          observation.Zoom = zoom;
        return observation;
      }));
      return response;
    }

    /// Replaces the camera's observations whole. Every landmark must be usable by the camera, see
    /// <see cref="EnsureLandmarksUsableAsync"/>.
    public async Task SetObservationsAsync(PtzGeoObservations observations, CancellationToken ct)
    {
      var cameraId = observations.CameraId;
      await EnsureCameraMayBeCalibratedAsync(cameraId, ct);
      var landmarkIds = ParseObservedLandmarks(observations.Observations.Select(o => o.LandmarkId));
      if (observations.Observations.Any(o => !float.IsFinite(o.Pan) || !float.IsFinite(o.Tilt)))
        throw new SpatialConfigException("Pan and tilt must be numbers");
      if (observations.Observations.Any(o => o.HasZoom && !float.IsFinite(o.Zoom)))
        throw new SpatialConfigException("Zoom must be a number");

      await using var tx = await BeginTransactionAsync(ct);
      await EnsureLandmarksUsableAsync(cameraId, landmarkIds, ct);

      _db.PtzGeoObservations.RemoveRange(
        await _db.PtzGeoObservations.Where(o => o.camera_id == cameraId).ToListAsync(ct));
      foreach (var o in observations.Observations)
      {
        _db.PtzGeoObservations.Add(new DBPtzGeoObservation
        {
          camera_id = cameraId,
          landmark_id = Guid.Parse(o.LandmarkId),
          pan = o.Pan,
          tilt = o.Tilt,
          zoom = o.HasZoom ? o.Zoom : null,
        });
      }

      await _db.SaveChangesAsync(ct);
      if (tx is not null)
        await tx.CommitAsync(ct);

      await NotifyAsync([], [cameraId], ct);
    }

    // ── Ground observations ───────────────────────────────────────────────────

    public async Task<GroundObservations> GetGroundObservationsAsync(string cameraId, CancellationToken ct)
    {
      var rows = await _db.GroundObservations.AsNoTracking()
        .Where(o => o.camera_id == cameraId)
        .ToListAsync(ct);

      var response = new GroundObservations { CameraId = cameraId };
      response.Observations.AddRange(rows.Select(o => new GroundObservation
      {
        LandmarkId = o.landmark_id.ToString(),
        X = o.x,
        Y = o.y,
      }));
      return response;
    }

    /// Replaces the camera's ground marks whole, under the same rules as PTZ observations.
    public async Task SetGroundObservationsAsync(GroundObservations observations, CancellationToken ct)
    {
      var cameraId = observations.CameraId;
      await EnsureCameraMayBeCalibratedAsync(cameraId, ct);
      var landmarkIds = ParseObservedLandmarks(observations.Observations.Select(o => o.LandmarkId));
      if (observations.Observations.Any(o => !(o.X is >= 0 and <= 1) || !(o.Y is >= 0 and <= 1)))
        throw new SpatialConfigException("A mark must lie within the picture: x and y between 0 and 1");

      await using var tx = await BeginTransactionAsync(ct);
      await EnsureLandmarksUsableAsync(cameraId, landmarkIds, ct);

      _db.GroundObservations.RemoveRange(
        await _db.GroundObservations.Where(o => o.camera_id == cameraId).ToListAsync(ct));
      foreach (var o in observations.Observations)
      {
        _db.GroundObservations.Add(new DBGroundObservation
        {
          camera_id = cameraId,
          landmark_id = Guid.Parse(o.LandmarkId),
          x = o.X,
          y = o.Y,
        });
      }

      await _db.SaveChangesAsync(ct);
      if (tx is not null)
        await tx.CommitAsync(ct);

      await NotifyAsync([], [cameraId], ct);
    }

    private static List<Guid> ParseObservedLandmarks(IEnumerable<string> ids)
    {
      var landmarkIds = ids.Select(id => ParseId(id, "landmark")).ToList();
      if (landmarkIds.Distinct().Count() != landmarkIds.Count)
        throw new SpatialConfigException("A landmark can be observed only once");
      return landmarkIds;
    }

    /// <summary>
    /// A camera that takes its place from the device has no fixed point to fit a calibration to:
    /// the marks would describe where it stood when they were made, not where it is now. Refused
    /// for both the ground marks and the PTZ landmark observations.
    /// </summary>
    private async Task EnsureCameraMayBeCalibratedAsync(string cameraId, CancellationToken ct)
    {
      var camera = await _cameraCatalog.GetAsync(cameraId, ct)
        ?? throw new SpatialConfigException($"Camera '{cameraId}' does not exist");
      if (camera.GeoFromDevice)
        throw new SpatialConfigException(
          "The camera takes its position from the device, so it cannot be calibrated against landmarks");
    }

    /// Every landmark must be in a space that shares geometry with the one the camera really stands
    /// in — an observation of a landmark in an unrelated plan describes nothing it can see.
    private async Task EnsureLandmarksUsableAsync(string cameraId, List<Guid> landmarkIds, CancellationToken ct)
    {
      if (landmarkIds.Count == 0)
        return;

      var cameraSpaceId = await (from p in _db.CameraPlacements.AsNoTracking()
                                 join g in _db.GeoObjects.AsNoTracking() on p.object_id equals g.id
                                 where p.camera_id == cameraId && p.is_reference
                                 select g.canvas_id).FirstOrDefaultAsync(ct);
      if (cameraSpaceId is null)
        throw new SpatialConfigException(
          "Mark where the camera really stands (its reference placement) before calibrating it");

      var cameraSpace = await _db.Spaces.AsNoTracking()
        .FirstOrDefaultAsync(s => s.object_id == cameraSpaceId, ct)
        ?? throw new SpatialConfigException(
          "Mark where the camera really stands (its reference placement) before calibrating it");

      var landmarkSpaces = await (from l in _db.Landmarks.AsNoTracking()
                                  join g in _db.GeoObjects.AsNoTracking() on l.object_id equals g.id
                                  join s in _db.Spaces.AsNoTracking() on g.canvas_id equals s.object_id
                                  where landmarkIds.Contains(l.object_id)
                                  select new { s.object_id, s.real_coordinates }).ToListAsync(ct);

      var compatible = landmarkSpaces.Count(s =>
        s.object_id == cameraSpace.object_id ||
        (cameraSpace.real_coordinates && s.real_coordinates));
      if (compatible != landmarkIds.Count)
        throw new SpatialConfigException(
          "Every landmark must exist in the camera's space, or in another space with real coordinates");
    }

    // ── Spatial models ────────────────────────────────────────────────────────

    /// Reference placement, its space and the observations of landmarks sharing its geometry, joined
    /// in one place so the aiming service needs a single round trip per camera.
    public async Task<CameraSpatialModelResponse> GetCameraSpatialModelAsync(string cameraId, CancellationToken ct)
    {
      var placed = await PlacedReferenceAsync(cameraId, ct);
      if (placed is null)
        return new CameraSpatialModelResponse { Found = false };

      var models = await BuildSpatialModelsAsync([placed.Value], ct);
      return models.Count > 0 ? models[0] : new CameraSpatialModelResponse { Found = false };
    }

    /// The models of the cameras whose detections can be shown in a space: those with ground marks,
    /// standing where they really are in a space that shares geometry with it.
    public async Task<ListCameraSpatialModelsResponse> ListCameraSpatialModelsAsync(Guid spaceId, CancellationToken ct)
    {
      var space = await _db.Spaces.AsNoTracking().FirstOrDefaultAsync(s => s.object_id == spaceId, ct)
        ?? throw new SpatialConfigException($"Space '{spaceId}' does not exist");

      var marked = _db.GroundObservations.Select(o => o.camera_id);
      var placed = await (from p in _db.CameraPlacements.AsNoTracking()
                          join g in _db.GeoObjects.AsNoTracking() on p.object_id equals g.id
                          join s in _db.Spaces.AsNoTracking() on g.canvas_id equals s.object_id
                          where p.is_reference && marked.Contains(p.camera_id)
                          select new { p, s }).ToListAsync(ct);

      var sharing = placed
        .Where(x => x.s.object_id == spaceId ||
                    (space.real_coordinates && x.s.real_coordinates))
        .Select(x => (x.p, x.s))
        .ToList();

      var response = new ListCameraSpatialModelsResponse();
      response.Models.AddRange(await BuildSpatialModelsAsync(sharing, ct));
      return response;
    }

    private async Task<(DBCameraPlacement Placement, DBSpace Space)?> PlacedReferenceAsync(
      string cameraId, CancellationToken ct)
    {
      var found = await (from p in _db.CameraPlacements.AsNoTracking()
                         join g in _db.GeoObjects.AsNoTracking() on p.object_id equals g.id
                         join s in _db.Spaces.AsNoTracking() on g.canvas_id equals s.object_id
                         where p.camera_id == cameraId && p.is_reference
                         select new { p, s }).FirstOrDefaultAsync(ct);
      return found is null ? null : (found.p, found.s);
    }

    /// Two queries however many cameras there are: their observations are read together and sorted
    /// out here, each kept only where its landmark shares geometry with that camera's own space.
    private async Task<List<CameraSpatialModelResponse>> BuildSpatialModelsAsync(
      IReadOnlyList<(DBCameraPlacement Placement, DBSpace Space)> placed,
      CancellationToken ct)
    {
      var result = new List<CameraSpatialModelResponse>();
      if (placed.Count == 0)
        return result;

      var cameraIds = placed.Select(x => x.Placement.camera_id).Distinct().ToList();
      var points = await LoadPointsAsync(placed.Select(x => x.Placement.object_id).ToList(), ct);

      var observations = (await (from o in _db.PtzGeoObservations.AsNoTracking()
                                 join l in _db.Landmarks.AsNoTracking() on o.landmark_id equals l.object_id
                                 join m in _db.Markers.AsNoTracking() on l.object_id equals m.id
                                 join g in _db.GeoObjects.AsNoTracking() on l.object_id equals g.id
                                 join s in _db.Spaces.AsNoTracking() on g.canvas_id equals s.object_id
                                 where cameraIds.Contains(o.camera_id)
                                 select new { o, l, m, s }).ToListAsync(ct))
        .ToLookup(x => x.o.camera_id, StringComparer.Ordinal);

      var ground = (await (from o in _db.GroundObservations.AsNoTracking()
                           join l in _db.Landmarks.AsNoTracking() on o.landmark_id equals l.object_id
                           join m in _db.Markers.AsNoTracking() on l.object_id equals m.id
                           join g in _db.GeoObjects.AsNoTracking() on l.object_id equals g.id
                           join s in _db.Spaces.AsNoTracking() on g.canvas_id equals s.object_id
                           where cameraIds.Contains(o.camera_id)
                           select new { o, l, m, s }).ToListAsync(ct))
        .ToLookup(x => x.o.camera_id, StringComparer.Ordinal);

      var landmarkIds = observations.SelectMany(x => x).Select(x => x.l.object_id)
        .Concat(ground.SelectMany(x => x).Select(x => x.l.object_id))
        .Distinct().ToList();
      var landmarkPoints = await LoadPointsAsync(landmarkIds, ct);

      var spaceIds = placed.Select(x => x.Space.object_id).Distinct().ToList();
      var spaceObjects = await _db.Markers.AsNoTracking()
        .Where(m => spaceIds.Contains(m.id))
        .ToDictionaryAsync(m => m.id, ct);
      var footprints = await LoadFootprintsAsync(spaceIds, ct);

      static bool SharesGeometry(DBSpace a, DBSpace b) =>
        a.object_id == b.object_id || (a.real_coordinates && b.real_coordinates);

      foreach (var (placement, space) in placed)
      {
        if (!points.TryGetValue(placement.object_id, out var point))
          continue;
        if (!spaceObjects.TryGetValue(space.object_id, out var spaceObject))
          continue;

        var model = new CameraSpatialModelResponse
        {
          Found = true,
          Placement = ToPlacementProto(placement, space.object_id, point.Lat, point.Lon),
          Space = ToProto(space, spaceObject, null, footprints.GetValueOrDefault(space.object_id)),
        };

        foreach (var y in observations[placement.camera_id].Where(y => SharesGeometry(space, y.s)))
        {
          if (!landmarkPoints.TryGetValue(y.l.object_id, out var landmarkPoint))
            continue;
          var observation = new GeoObservation
          {
            LandmarkId = y.l.object_id.ToString(),
            LandmarkName = y.m.name,
            Lat = landmarkPoint.Lat,
            Lon = landmarkPoint.Lon,
            HeightAboveGroundM = y.s.elevation_m + y.l.height_m,
            Pan = y.o.pan,
            Tilt = y.o.tilt,
          };
          if (y.o.zoom is { } zoom)
            observation.Zoom = zoom;
          model.Observations.Add(observation);
        }

        foreach (var y in ground[placement.camera_id].Where(y => SharesGeometry(space, y.s)))
        {
          if (!landmarkPoints.TryGetValue(y.l.object_id, out var landmarkPoint))
            continue;
          model.GroundObservations.Add(new GeoGroundObservation
          {
            LandmarkId = y.l.object_id.ToString(),
            LandmarkName = y.m.name,
            Lat = landmarkPoint.Lat,
            Lon = landmarkPoint.Lon,
            FloorElevationM = y.s.elevation_m,
            X = y.o.x,
            Y = y.o.y,
          });
        }

        result.Add(model);
      }

      return result;
    }

    // ── Placement helpers ─────────────────────────────────────────────────────

    /// The placement row this camera has on this canvas, whichever node carries it.
    private async Task<DBCameraPlacement?> RowInSpaceAsync(string cameraId, Guid spaceId, CancellationToken ct) =>
      await (from p in _db.CameraPlacements
             join g in _db.GeoObjects on p.object_id equals g.id
             where p.camera_id == cameraId && g.canvas_id == spaceId
             select p).FirstOrDefaultAsync(ct);

    /// The camera's canonical node, under its server. A placement write can reach a camera the
    /// adapter has not seen yet, and the canonical node must exist and stand where the model says
    /// -- under the server, never under the space it is drawn in -- before a replica can name it.
    private async Task EnsureCanonicalNodeAsync(Guid canonicalId, CameraInfo camera, CancellationToken ct)
    {
      var serverId = string.IsNullOrWhiteSpace(camera.ServerId)
        ? null
        : ServerNodeId.For(camera.ServerId);

      var marker = await _db.Markers.AsNoTracking().FirstOrDefaultAsync(m => m.id == canonicalId, ct);
      if (marker is null || marker.parent_id != Utils.ConvertObjectIdToGuid(serverId))
      {
        await _mapService.UpdateHierarchyAsync([new BaseMarkerDTO
        {
          id = canonicalId.ToString(),
          parent_id = serverId,
          name = camera.Name,
        }]);
      }

      await EnsureDeviceNodeAsync(canonicalId, camera.CameraId, ct);
    }

    // The reverse index the tree needs to name a camera whose node id is an MD5 of its camera id.
    // A placement write is the other path that can reach a camera node the adapter has not seen.
    private async Task EnsureDeviceNodeAsync(Guid canonicalId, string cameraId, CancellationToken ct)
    {
      var row = await _db.DeviceNodes.FirstOrDefaultAsync(n => n.object_id == canonicalId, ct);
      if (row is null)
      {
        _db.DeviceNodes.Add(new DBDeviceNode
        {
          object_id = canonicalId,
          source_kind = DeviceObjectId.CameraSource,
          source_id = cameraId,
          part_kind = DeviceObjectId.DevicePart,
          part_token = string.Empty,
        });
      }
      else
      {
        row.source_kind = DeviceObjectId.CameraSource;
        row.source_id = cameraId;
        row.part_kind = DeviceObjectId.DevicePart;
        row.part_token = string.Empty;
      }
    }

    /// Leaves a picture where the camera stood before: a replica of the camera node with a copy of
    /// the placement it had.
    /// Removes one picture: its row, its point and its node. A picture is a leaf, so nothing else
    /// hangs from it.
    private async Task DeletePictureAsync(Guid objectId, CancellationToken ct)
    {
      _db.CameraPlacements.RemoveRange(
        await _db.CameraPlacements.Where(p => p.object_id == objectId).ToListAsync(ct));
      _db.GeoObjects.RemoveRange(
        await _db.GeoObjects.Where(g => g.id == objectId).ToListAsync(ct));
      _db.Markers.RemoveRange(
        await _db.Markers.Where(m => m.id == objectId).ToListAsync(ct));
      await _db.SaveChangesAsync(ct);
    }

    private static void ApplyValues(DBCameraPlacement row, CameraPlacement placement)
    {
      row.height_m = placement.HeightM;
      row.heading_deg = NormalizeBearing(placement.HeadingDeg);
      row.hfov_deg = placement.HfovDeg;
      row.range_m = placement.RangeM;
    }

    private async Task<List<string>> ObserversOfLandmarkAsync(
      IReadOnlyCollection<Guid> landmarkIds, CancellationToken ct)
    {
      if (landmarkIds.Count == 0)
        return [];

      var observers = new HashSet<string>(StringComparer.Ordinal);
      observers.UnionWith(await _db.PtzGeoObservations.AsNoTracking()
        .Where(o => landmarkIds.Contains(o.landmark_id))
        .Select(o => o.camera_id).ToListAsync(ct));
      observers.UnionWith(await _db.GroundObservations.AsNoTracking()
        .Where(o => landmarkIds.Contains(o.landmark_id))
        .Select(o => o.camera_id).ToListAsync(ct));
      return observers.ToList();
    }

    // ── Geometry helpers ─────────────────────────────────────────────────────

    /// The canvas a node's figure is drawn on: the space it is really in, whichever node holds it
    /// as a child. Null when the node has no figure or no canvas ancestor.
    private async Task<Guid?> CanvasOfAsync(Guid objectId, CancellationToken ct) =>
      await _db.GeoObjects.AsNoTracking()
        .Where(g => g.id == objectId)
        .Select(g => g.canvas_id)
        .FirstOrDefaultAsync(ct);

    private async Task<Dictionary<Guid, (double Lat, double Lon)>> LoadPointsAsync(
      IReadOnlyCollection<Guid> objectIds, CancellationToken ct)
    {
      var result = new Dictionary<Guid, (double Lat, double Lon)>();
      if (objectIds.Count == 0)
        return result;

      var ids = objectIds.Distinct().ToList();
      var rows = await _db.GeoObjects.AsNoTracking()
        .Where(g => ids.Contains(g.id))
        .ToListAsync(ct);
      foreach (var row in rows)
      {
        if (row.figure is NtsPoint point)
          result[row.id] = (point.Y, point.X);
      }
      return result;
    }

    private async Task<Dictionary<Guid, List<Coordinate>>> LoadFootprintsAsync(
      IReadOnlyCollection<Guid> spaceIds, CancellationToken ct)
    {
      var result = new Dictionary<Guid, List<Coordinate>>();
      if (spaceIds.Count == 0)
        return result;

      var ids = spaceIds.Distinct().ToList();
      var rows = await _db.GeoObjects.AsNoTracking()
        .Where(g => ids.Contains(g.id))
        .ToListAsync(ct);
      foreach (var row in rows)
      {
        if (row.figure is not NtsPolygon polygon)
          continue;
        // The stored ring is closed; the protocol's polygon is open, as GeoJSON's is.
        var ring = polygon.ExteriorRing.Coordinates;
        result[row.id] = ring
          .Take(Math.Max(0, ring.Length - 1))
          .Select(c => new Coordinate { Lat = c.Y, Lon = c.X })
          .ToList();
      }
      return result;
    }

    private static FigureGeoDTO PointFigure(Guid objectId, double lat, double lon) => new()
    {
      id = objectId.ToString(),
      geometry = new GeometryCircleDTO(new Geo2DCoordDTO { Lat = lat, Lon = lon }),
    };

    private static FigureGeoDTO PolygonFigure(Guid objectId, List<(double Lat, double Lon)> points) => new()
    {
      id = objectId.ToString(),
      geometry = new GeometryPolygonDTO
      {
        coord = points.Select(p => new Geo2DCoordDTO { Lat = p.Lat, Lon = p.Lon }).ToList(),
      },
    };

    // ── Mapping and validation ────────────────────────────────────────────────

    private static void RequireLatLon(double lat, double lon, string what)
    {
      if (!(lat >= -90 && lat <= 90 && lon >= -180 && lon <= 180))
        throw new SpatialConfigException($"The {what} position is not a valid latitude/longitude");
    }

    private static void RequireCorners(PlanCorners? c)
    {
      if (c?.TopLeft is null || c.TopRight is null || c.BottomLeft is null)
        throw new SpatialConfigException("A plan needs three corners");
      RequireLatLon(c.TopLeft.Lat, c.TopLeft.Lon, "corner");
      RequireLatLon(c.TopRight.Lat, c.TopRight.Lon, "corner");
      RequireLatLon(c.BottomLeft.Lat, c.BottomLeft.Lon, "corner");
    }

    private static void RequireView(GeoBounds? view)
    {
      if (view?.SouthWest is null || view.NorthEast is null)
        throw new SpatialConfigException("A space needs a default view");
      RequireLatLon(view.SouthWest.Lat, view.SouthWest.Lon, "view corner");
      RequireLatLon(view.NorthEast.Lat, view.NorthEast.Lon, "view corner");
      // A view across the antimeridian would need west > east; nothing is there, so it is refused
      // rather than half-supported.
      if (view.SouthWest.Lat >= view.NorthEast.Lat || view.SouthWest.Lon >= view.NorthEast.Lon)
        throw new SpatialConfigException(
          "The default view must have its south-west corner below and left of its north-east one");
    }

    /// The footprint of a nested space, in its parent's coordinates; null when none is drawn.
    private static List<(double Lat, double Lon)>? ReadFootprint(
      IEnumerable<Coordinate> footprint, Guid? parentId)
    {
      var points = footprint.ToList();
      if (points.Count == 0)
        return null;
      if (parentId is null)
        throw new SpatialConfigException("A root space has no parent to draw a footprint on");
      if (points.Count < 3)
        throw new SpatialConfigException("A footprint needs at least three corners");
      foreach (var point in points)
        RequireLatLon(point.Lat, point.Lon, "footprint corner");
      return points.Select(p => (p.Lat, p.Lon)).ToList();
    }

    private static bool IsSelfOrDescendant(
      IReadOnlyDictionary<Guid, Guid?> parents, Guid id, Guid ancestor)
    {
      // Bounded by the number of spaces, so a loop already in the data cannot hang the check.
      Guid? at = id;
      for (var steps = 0; at is not null && steps <= parents.Count; steps++)
      {
        if (at == ancestor)
          return true;
        at = parents.GetValueOrDefault(at.Value);
      }
      return false;
    }

    /// Plan metadata without the image bytes, keyed by space; one space when spaceId is given.
    private async Task<Dictionary<Guid, SpacePlan>> LoadPlansAsync(Guid? spaceId, CancellationToken ct)
    {
      var rows = await _db.SpacePlans.AsNoTracking()
        .Where(p => spaceId == null || p.space_id == spaceId)
        .Select(p => new DBSpacePlan
        {
          space_id = p.space_id,
          mime = p.mime,
          width_px = p.width_px,
          height_px = p.height_px,
          tl_lat = p.tl_lat,
          tl_lon = p.tl_lon,
          tr_lat = p.tr_lat,
          tr_lon = p.tr_lon,
          bl_lat = p.bl_lat,
          bl_lon = p.bl_lon,
          etag = p.etag,
        })
        .ToListAsync(ct);
      return rows.ToDictionary(r => r.space_id, ToPlan);
    }

    private static SpacePlan ToPlan(DBSpacePlan p) => new()
    {
      Corners = new PlanCorners
      {
        TopLeft = new Coordinate { Lat = p.tl_lat, Lon = p.tl_lon },
        TopRight = new Coordinate { Lat = p.tr_lat, Lon = p.tr_lon },
        BottomLeft = new Coordinate { Lat = p.bl_lat, Lon = p.bl_lon },
      },
      WidthPx = (uint)p.width_px,
      HeightPx = (uint)p.height_px,
      Mime = p.mime,
      Etag = p.etag,
    };

    private static void SetCorners(DBSpacePlan row, PlanCorners c)
    {
      row.tl_lat = c.TopLeft.Lat; row.tl_lon = c.TopLeft.Lon;
      row.tr_lat = c.TopRight.Lat; row.tr_lon = c.TopRight.Lon;
      row.bl_lat = c.BottomLeft.Lat; row.bl_lon = c.BottomLeft.Lon;
    }

    private static Space ToProto(DBSpace s, DBMarker o, SpacePlan? plan, List<Coordinate>? footprint)
    {
      var space = new Space
      {
        Id = o.id.ToString(),
        ParentId = o.parent_id?.ToString() ?? string.Empty,
        Name = o.name ?? string.Empty,
        Kind = s.kind == SpaceKinds.Map ? SpaceKind.Map : SpaceKind.Plan,
        RealCoordinates = s.real_coordinates,
        Ordinal = s.ordinal,
        ElevationM = s.elevation_m,
        View = new GeoBounds
        {
          SouthWest = new Coordinate { Lat = s.view_south, Lon = s.view_west },
          NorthEast = new Coordinate { Lat = s.view_north, Lon = s.view_east },
        },
      };
      if (footprint is not null)
        space.Footprint.AddRange(footprint);
      if (plan is not null)
        space.Plan = plan;
      return space;
    }

    private static CameraPlacement ToPlacementProto(
      DBCameraPlacement p, Guid spaceId, double lat, double lon) => new()
    {
      CameraId = p.camera_id,
      SpaceId = spaceId.ToString(),
      IsReference = p.is_reference,
      Lat = lat,
      Lon = lon,
      HeightM = p.height_m,
      HeadingDeg = p.heading_deg,
      HfovDeg = p.hfov_deg,
      RangeM = p.range_m,
    };

    private static Landmark ToProto(DBLandmark l, Guid spaceId, string? name, double lat, double lon) => new()
    {
      Id = l.object_id.ToString(),
      SpaceId = spaceId.ToString(),
      Name = name ?? string.Empty,
      Lat = lat,
      Lon = lon,
      HeightM = l.height_m,
    };

    private static Guid ParseId(string raw, string what) =>
      Guid.TryParse(raw, out var id)
        ? id
        : throw new SpatialConfigException($"'{raw}' is not a valid {what} id");

    private static double NormalizeBearing(double degrees)
    {
      var d = degrees % 360;
      return d < 0 ? d + 360 : d;
    }

    // ── Plumbing ─────────────────────────────────────────────────────────────

    // InMemory, which the tests use, has no transactions; production always runs on PostgreSQL.
    // A caller that already opened one — the demo install wraps a whole world in a single
    // transaction — owns it: the steps here join it, and a null answer says the commit is not
    // theirs to make. Opening a second would fail on the connection before the first step ran.
    private async Task<IDbContextTransaction?> BeginTransactionAsync(CancellationToken ct) =>
      !_db.Database.IsRelational() || _db.Database.CurrentTransaction is not null
        ? null
        : await _db.Database.BeginTransactionAsync(ct);

    private Task NotifyAsync(
      IEnumerable<string> objectIds, IEnumerable<string> cameraIds, CancellationToken ct) =>
      _notifier.PublishAsync(
        objectIds.Distinct(StringComparer.Ordinal).ToList(),
        cameraIds.Distinct(StringComparer.Ordinal).ToList(),
        ct);
  }
}
