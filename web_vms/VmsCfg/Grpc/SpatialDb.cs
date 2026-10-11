using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Vms.Cfg;

namespace VmsCfg;

/// A request that cannot be stored as asked: a missing parent, a value out of range, a loop in the
/// tree. The message is meant for the administrator who sent it.
public sealed class SpatialConfigException(string message) : Exception(message);

/// Spaces, plan images, camera placements, landmarks and PTZ observations of landmarks.
/// See migrations/0002_spatial.sql for what each table means.
public class SpatialDb(IDbContextFactory<VmsCfgDbContext> dbFactory)
{
  // ── Spaces ────────────────────────────────────────────────────────────────

  public async Task<ListSpacesResponse> ListSpacesAsync(CancellationToken ct)
  {
    await using var db = await dbFactory.CreateDbContextAsync(ct);
    var spaces = await db.Spaces.AsNoTracking().OrderBy(s => s.Ordinal).ThenBy(s => s.Name).ToListAsync(ct);
    var plans = await LoadPlansAsync(db, null, ct);

    var response = new ListSpacesResponse();
    response.Spaces.AddRange(spaces.Select(s => ToProto(s, plans.GetValueOrDefault(s.Id))));
    return response;
  }

  public async Task<Space?> GetSpaceAsync(Guid spaceId, CancellationToken ct)
  {
    await using var db = await dbFactory.CreateDbContextAsync(ct);
    var row = await db.Spaces.AsNoTracking().FirstOrDefaultAsync(s => s.Id == spaceId, ct);
    if (row is null)
      return null;
    var plans = await LoadPlansAsync(db, spaceId, ct);
    return ToProto(row, plans.GetValueOrDefault(spaceId));
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
    Guid? parentId = string.IsNullOrEmpty(space.ParentId) ? null : ParseId(space.ParentId, "parent space");
    var kind = space.Kind == SpaceKind.Map ? SpaceKinds.Map : SpaceKinds.Plan;
    if (kind == SpaceKinds.Map && parentId is not null)
      throw new SpatialConfigException("A map is always a root; a space inside another one is a plan");
    var footprint = ReadFootprint(space, parentId);

    await using var db = await dbFactory.CreateDbContextAsync(ct);
    await using var tx = await db.Database.BeginTransactionAsync(ct);

    DbSpace? row = null;
    if (!string.IsNullOrEmpty(space.Id))
    {
      var id = ParseId(space.Id, "space");
      row = await db.Spaces.FirstOrDefaultAsync(s => s.Id == id, ct)
        ?? throw new SpatialConfigException($"Space '{space.Id}' does not exist");
      if (row.Kind != kind)
        throw new SpatialConfigException("A space cannot change between map and plan");
    }

    if (parentId is not null)
    {
      // Load the tree once: spaces number in the dozens, and walking it in memory is what makes the
      // loop check cheap to get right.
      var parents = await db.Spaces.AsNoTracking().ToDictionaryAsync(s => s.Id, s => s.ParentId, ct);
      if (!parents.ContainsKey(parentId.Value))
        throw new SpatialConfigException($"Space '{space.ParentId}' does not exist");
      if (row is not null && IsSelfOrDescendant(parents, parentId.Value, row.Id))
        throw new SpatialConfigException("A space cannot be put inside itself");
    }

    var name = space.Name.Trim();
    if (await db.Spaces.AnyAsync(s => s.ParentId == parentId && s.Name == name && (row == null || s.Id != row.Id), ct))
      throw new SpatialConfigException($"There already is a space named '{name}' here");

    if (row is null)
    {
      row = new DbSpace { Id = Guid.NewGuid(), Kind = kind };
      db.Spaces.Add(row);
    }

    row.ParentId = parentId;
    row.Name = name;
    // A map is real by definition: its background is the real world.
    row.RealCoordinates = kind == SpaceKinds.Map || space.RealCoordinates;
    row.Ordinal = space.Ordinal;
    row.ElevationM = space.ElevationM;
    row.ViewSouth = space.View.SouthWest.Lat;
    row.ViewWest = space.View.SouthWest.Lon;
    row.ViewNorth = space.View.NorthEast.Lat;
    row.ViewEast = space.View.NorthEast.Lon;
    row.Footprint = footprint;

    await db.SaveChangesAsync(ct);
    await tx.CommitAsync(ct);

    var plans = await LoadPlansAsync(db, row.Id, ct);
    return ToProto(row, plans.GetValueOrDefault(row.Id));
  }

  /// Removes the space with everything nested in it; the database cascades.
  public async Task<bool> RemoveSpaceAsync(Guid spaceId, CancellationToken ct)
  {
    await using var db = await dbFactory.CreateDbContextAsync(ct);
    return await db.Spaces.Where(s => s.Id == spaceId).ExecuteDeleteAsync(ct) > 0;
  }

  /// The space and every space nested in it, at any depth.
  public async Task<List<Guid>> SubtreeAsync(Guid spaceId, CancellationToken ct)
  {
    await using var db = await dbFactory.CreateDbContextAsync(ct);
    var parents = await db.Spaces.AsNoTracking().ToDictionaryAsync(s => s.Id, s => s.ParentId, ct);
    return parents.Keys.Where(id => IsSelfOrDescendant(parents, id, spaceId)).ToList();
  }

  /// Cameras drawn in any of these spaces. Read before a removal cascades, so their nodes can be
  /// told the placements are gone.
  public async Task<List<string>> ListPlacedCamerasAsync(IReadOnlyCollection<Guid> spaceIds, CancellationToken ct)
  {
    await using var db = await dbFactory.CreateDbContextAsync(ct);
    return await db.CameraPlacements
      .Where(p => spaceIds.Contains(p.SpaceId))
      .Select(p => p.CameraId)
      .Distinct()
      .ToListAsync(ct);
  }

  public async Task<Guid?> SpaceIdOfLandmarkAsync(Guid landmarkId, CancellationToken ct)
  {
    await using var db = await dbFactory.CreateDbContextAsync(ct);
    return await db.Landmarks.Where(m => m.Id == landmarkId).Select(m => (Guid?)m.SpaceId).FirstOrDefaultAsync(ct);
  }

  // ── Plan images ───────────────────────────────────────────────────────────

  public async Task<Space> SavePlanAsync(SpacePlanUploadHeader header, byte[] image, CancellationToken ct)
  {
    var spaceId = ParseId(header.SpaceId, "space");
    RequireCorners(header.Corners);
    if (header.WidthPx == 0 || header.HeightPx == 0)
      throw new SpatialConfigException("The image size must be given");

    await using var db = await dbFactory.CreateDbContextAsync(ct);
    var space = await db.Spaces.AsNoTracking().FirstOrDefaultAsync(s => s.Id == spaceId, ct)
      ?? throw new SpatialConfigException($"Space '{header.SpaceId}' does not exist");
    if (space.Kind != SpaceKinds.Plan)
      throw new SpatialConfigException("A map is drawn on map tiles; only a plan has a picture");

    var row = await db.SpacePlans.FirstOrDefaultAsync(p => p.SpaceId == spaceId, ct);
    if (row is null)
    {
      row = new DbSpacePlan { SpaceId = spaceId };
      db.SpacePlans.Add(row);
    }

    row.Mime = header.Mime;
    row.WidthPx = (int)header.WidthPx;
    row.HeightPx = (int)header.HeightPx;
    SetCorners(row, header.Corners);
    row.Etag = Convert.ToHexStringLower(SHA256.HashData(image));
    row.Image = image;
    await db.SaveChangesAsync(ct);

    return ToProto(space, ToPlan(row));
  }

  public async Task<Space> SetPlanCornersAsync(Guid spaceId, PlanCorners corners, CancellationToken ct)
  {
    RequireCorners(corners);

    await using var db = await dbFactory.CreateDbContextAsync(ct);
    var space = await db.Spaces.AsNoTracking().FirstOrDefaultAsync(s => s.Id == spaceId, ct)
      ?? throw new SpatialConfigException($"Space '{spaceId}' does not exist");

    var updated = await db.SpacePlans.Where(p => p.SpaceId == spaceId).ExecuteUpdateAsync(u => u
      .SetProperty(p => p.TlLat, corners.TopLeft.Lat).SetProperty(p => p.TlLon, corners.TopLeft.Lon)
      .SetProperty(p => p.TrLat, corners.TopRight.Lat).SetProperty(p => p.TrLon, corners.TopRight.Lon)
      .SetProperty(p => p.BlLat, corners.BottomLeft.Lat).SetProperty(p => p.BlLon, corners.BottomLeft.Lon), ct);
    if (updated == 0)
      throw new SpatialConfigException("This space has no plan to align");

    var plans = await LoadPlansAsync(db, spaceId, ct);
    return ToProto(space, plans.GetValueOrDefault(spaceId));
  }

  /// The image with its type and ETag, or null when the space has no plan.
  public async Task<(string Mime, string Etag, byte[] Image)?> GetPlanImageAsync(
    Guid spaceId, string? ifNoneMatch, CancellationToken ct)
  {
    await using var db = await dbFactory.CreateDbContextAsync(ct);
    var head = await db.SpacePlans.AsNoTracking()
      .Where(p => p.SpaceId == spaceId)
      .Select(p => new { p.Mime, p.Etag })
      .FirstOrDefaultAsync(ct);
    if (head is null)
      return null;
    // The bytes are not read at all when the caller's copy is current.
    if (!string.IsNullOrEmpty(ifNoneMatch) && ifNoneMatch == head.Etag)
      return (head.Mime, head.Etag, []);

    var image = await db.SpacePlans.AsNoTracking()
      .Where(p => p.SpaceId == spaceId)
      .Select(p => p.Image)
      .FirstAsync(ct);
    return (head.Mime, head.Etag, image);
  }

  public async Task<bool> RemovePlanAsync(Guid spaceId, CancellationToken ct)
  {
    await using var db = await dbFactory.CreateDbContextAsync(ct);
    return await db.SpacePlans.Where(p => p.SpaceId == spaceId).ExecuteDeleteAsync(ct) > 0;
  }

  // ── Camera placements ─────────────────────────────────────────────────────

  public async Task<List<CameraPlacement>> ListPlacementsAsync(CancellationToken ct)
  {
    await using var db = await dbFactory.CreateDbContextAsync(ct);
    var rows = await db.CameraPlacements.AsNoTracking().ToListAsync(ct);
    return rows.Select(ToProto).ToList();
  }

  /// Where the camera really stands and the space that is in; null when it has no reference.
  public async Task<(CameraPlacement Placement, Space Space)?> GetReferencePlacementAsync(string cameraId, CancellationToken ct)
  {
    await using var db = await dbFactory.CreateDbContextAsync(ct);
    var found = await db.CameraPlacements.AsNoTracking()
      .Where(p => p.CameraId == cameraId && p.IsReference)
      .Join(db.Spaces, p => p.SpaceId, s => s.Id, (p, s) => new { p, s })
      .FirstOrDefaultAsync(ct);
    return found is null ? null : (ToProto(found.p), ToProto(found.s, null));
  }

  /// Stores one placement. A camera's first placement becomes its reference; making a placement
  /// the reference takes the flag from whichever one had it. Clearing the flag leaves the camera
  /// with no reference at all — another placement is never promoted behind the administrator's back,
  /// since that would quietly change where the camera is aimed from.
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
    var spaceId = ParseId(placement.SpaceId, "space");

    await using var db = await dbFactory.CreateDbContextAsync(ct);
    await using var tx = await db.Database.BeginTransactionAsync(ct);
    if (!await db.CameraSetups.AnyAsync(c => c.CameraId == placement.CameraId, ct))
      throw new SpatialConfigException($"Camera '{placement.CameraId}' does not exist");
    if (!await db.Spaces.AnyAsync(s => s.Id == spaceId, ct))
      throw new SpatialConfigException($"Space '{placement.SpaceId}' does not exist");

    var row = await db.CameraPlacements.FirstOrDefaultAsync(p => p.CameraId == placement.CameraId && p.SpaceId == spaceId, ct);
    var isReference = placement.IsReference;
    if (row is null)
    {
      isReference |= !await db.CameraPlacements.AnyAsync(p => p.CameraId == placement.CameraId, ct);
      row = new DbCameraPlacement { CameraId = placement.CameraId, SpaceId = spaceId };
      db.CameraPlacements.Add(row);
    }

    if (isReference)
    {
      // Before the row is saved: the unique index admits one reference per camera at any moment.
      await db.CameraPlacements
        .Where(p => p.CameraId == placement.CameraId && p.SpaceId != spaceId && p.IsReference)
        .ExecuteUpdateAsync(u => u.SetProperty(p => p.IsReference, false), ct);
    }

    row.IsReference = isReference;
    row.Lat = placement.Lat;
    row.Lon = placement.Lon;
    row.HeightM = placement.HeightM;
    row.HeadingDeg = NormalizeBearing(placement.HeadingDeg);
    row.HfovDeg = placement.HfovDeg;
    row.RangeM = placement.RangeM;
    await db.SaveChangesAsync(ct);
    await tx.CommitAsync(ct);
  }

  public async Task<bool> RemovePlacementAsync(string cameraId, Guid spaceId, CancellationToken ct)
  {
    await using var db = await dbFactory.CreateDbContextAsync(ct);
    return await db.CameraPlacements.Where(p => p.CameraId == cameraId && p.SpaceId == spaceId).ExecuteDeleteAsync(ct) > 0;
  }

  // ── Landmarks ─────────────────────────────────────────────────────────────

  public async Task<List<Landmark>> ListLandmarksAsync(Guid spaceId, CancellationToken ct)
  {
    await using var db = await dbFactory.CreateDbContextAsync(ct);
    var rows = await db.Landmarks.AsNoTracking().Where(m => m.SpaceId == spaceId).OrderBy(m => m.Name).ToListAsync(ct);
    return rows.Select(ToProto).ToList();
  }

  public async Task<Landmark> UpsertLandmarkAsync(Landmark landmark, CancellationToken ct)
  {
    if (string.IsNullOrWhiteSpace(landmark.Name))
      throw new SpatialConfigException("A landmark needs a name");
    RequireLatLon(landmark.Lat, landmark.Lon, "landmark");
    if (!double.IsFinite(landmark.HeightM))
      throw new SpatialConfigException("Height must be a number");
    var spaceId = ParseId(landmark.SpaceId, "space");

    await using var db = await dbFactory.CreateDbContextAsync(ct);
    if (!await db.Spaces.AnyAsync(s => s.Id == spaceId, ct))
      throw new SpatialConfigException($"Space '{landmark.SpaceId}' does not exist");

    DbLandmark? row;
    if (string.IsNullOrEmpty(landmark.Id))
    {
      row = new DbLandmark { Id = Guid.NewGuid() };
      db.Landmarks.Add(row);
    }
    else
    {
      var id = ParseId(landmark.Id, "landmark");
      row = await db.Landmarks.FirstOrDefaultAsync(m => m.Id == id, ct)
        ?? throw new SpatialConfigException($"Landmark '{landmark.Id}' does not exist");
      // Moved to another space, every observation of it would describe a place its cameras
      // cannot see.
      if (row.SpaceId != spaceId)
        throw new SpatialConfigException("A landmark cannot move to another space");
    }

    row.SpaceId = spaceId;
    row.Name = landmark.Name.Trim();
    row.Lat = landmark.Lat;
    row.Lon = landmark.Lon;
    row.HeightM = landmark.HeightM;
    await db.SaveChangesAsync(ct);
    return ToProto(row);
  }

  public async Task<bool> RemoveLandmarkAsync(Guid landmarkId, CancellationToken ct)
  {
    await using var db = await dbFactory.CreateDbContextAsync(ct);
    return await db.Landmarks.Where(m => m.Id == landmarkId).ExecuteDeleteAsync(ct) > 0;
  }

  /// Cameras that hold an observation of this landmark, so their consumers can be told when it
  /// moves or goes.
  public async Task<List<string>> ListCamerasObservingAsync(Guid landmarkId, CancellationToken ct)
  {
    await using var db = await dbFactory.CreateDbContextAsync(ct);
    return await db.PtzGeoObservations
      .Where(o => o.LandmarkId == landmarkId)
      .Select(o => o.CameraId)
      .ToListAsync(ct);
  }

  // ── PTZ observations ──────────────────────────────────────────────────────

  public async Task<PtzGeoObservations> GetObservationsAsync(string cameraId, CancellationToken ct)
  {
    await using var db = await dbFactory.CreateDbContextAsync(ct);
    var rows = await db.PtzGeoObservations.AsNoTracking()
      .Where(o => o.CameraId == cameraId)
      .ToListAsync(ct);

    var response = new PtzGeoObservations { CameraId = cameraId };
    response.Observations.AddRange(rows.Select(o => new PtzGeoObservation
    {
      LandmarkId = o.LandmarkId.ToString(),
      Pan = o.Pan,
      Tilt = o.Tilt,
    }));
    return response;
  }

  /// Replaces the camera's observations whole. Every landmark must be in a space that shares
  /// geometry with the one the camera really stands in — an observation of a landmark in an
  /// unrelated plan describes nothing it can see.
  public async Task SetObservationsAsync(PtzGeoObservations observations, CancellationToken ct)
  {
    var cameraId = observations.CameraId;
    var landmarkIds = observations.Observations.Select(o => ParseId(o.LandmarkId, "landmark")).ToList();
    if (landmarkIds.Distinct().Count() != landmarkIds.Count)
      throw new SpatialConfigException("A landmark can be observed only once");
    if (observations.Observations.Any(o => !float.IsFinite(o.Pan) || !float.IsFinite(o.Tilt)))
      throw new SpatialConfigException("Pan and tilt must be numbers");

    await using var db = await dbFactory.CreateDbContextAsync(ct);
    await using var tx = await db.Database.BeginTransactionAsync(ct);

    if (landmarkIds.Count > 0)
    {
      var cameraSpace = await db.CameraPlacements
        .Where(p => p.CameraId == cameraId && p.IsReference)
        .Join(db.Spaces, p => p.SpaceId, s => s.Id, (p, s) => s)
        .FirstOrDefaultAsync(ct)
        ?? throw new SpatialConfigException("Mark where the camera really stands (its reference placement) before calibrating it");

      var compatible = await db.Landmarks
        .Where(m => landmarkIds.Contains(m.Id))
        .Join(db.Spaces, m => m.SpaceId, s => s.Id, (m, s) => s)
        .CountAsync(s => s.Id == cameraSpace.Id || (cameraSpace.RealCoordinates && s.RealCoordinates), ct);
      if (compatible != landmarkIds.Count)
        throw new SpatialConfigException("Every landmark must exist in the camera's space, or in another space with real coordinates");
    }

    await db.PtzGeoObservations.Where(o => o.CameraId == cameraId).ExecuteDeleteAsync(ct);
    foreach (var o in observations.Observations)
    {
      db.PtzGeoObservations.Add(new DbPtzGeoObservation
      {
        CameraId = cameraId,
        LandmarkId = Guid.Parse(o.LandmarkId),
        Pan = o.Pan,
        Tilt = o.Tilt,
      });
    }

    await db.SaveChangesAsync(ct);
    await tx.CommitAsync(ct);
  }

  /// Reference placement, its space and the observations of landmarks sharing its geometry, joined
  /// in one place so the aiming service needs a single round trip per camera.
  public async Task<CameraSpatialModelResponse> GetCameraSpatialModelAsync(string cameraId, CancellationToken ct)
  {
    await using var db = await dbFactory.CreateDbContextAsync(ct);
    var placed = await db.CameraPlacements.AsNoTracking()
      .Where(p => p.CameraId == cameraId && p.IsReference)
      .Join(db.Spaces, p => p.SpaceId, s => s.Id, (p, s) => new { p, s })
      .FirstOrDefaultAsync(ct);
    if (placed is null)
      return new CameraSpatialModelResponse { Found = false };

    var spaceId = placed.s.Id;
    var real = placed.s.RealCoordinates;
    var observations = await db.PtzGeoObservations.AsNoTracking()
      .Where(o => o.CameraId == cameraId)
      .Join(db.Landmarks, o => o.LandmarkId, m => m.Id, (o, m) => new { o, m })
      .Join(db.Spaces, x => x.m.SpaceId, s => s.Id, (x, s) => new { x.o, x.m, s })
      .Where(x => x.s.Id == spaceId || (real && x.s.RealCoordinates))
      .ToListAsync(ct);

    var response = new CameraSpatialModelResponse
    {
      Found = true,
      Placement = ToProto(placed.p),
      Space = ToProto(placed.s, null),
    };
    response.Observations.AddRange(observations.Select(x => new GeoObservation
    {
      LandmarkId = x.m.Id.ToString(),
      LandmarkName = x.m.Name,
      Lat = x.m.Lat,
      Lon = x.m.Lon,
      HeightAboveGroundM = x.s.ElevationM + x.m.HeightM,
      Pan = x.o.Pan,
      Tilt = x.o.Tilt,
    }));
    return response;
  }

  // ── Mapping and validation ────────────────────────────────────────────────

  /// Whether `id` is `ancestor` or lies anywhere beneath it.
  private static bool IsSelfOrDescendant(IReadOnlyDictionary<Guid, Guid?> parents, Guid id, Guid ancestor)
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

  /// The footprint as stored: JSON [[lat, lon], ...], or null when none is drawn.
  private static string? ReadFootprint(Space space, Guid? parentId)
  {
    if (space.Footprint.Count == 0)
      return null;
    if (parentId is null)
      throw new SpatialConfigException("A root space has no parent to draw a footprint on");
    if (space.Footprint.Count < 3)
      throw new SpatialConfigException("A footprint needs at least three corners");
    foreach (var p in space.Footprint)
      RequireLatLon(p.Lat, p.Lon, "footprint corner");
    return JsonSerializer.Serialize(space.Footprint.Select(p => new[] { p.Lat, p.Lon }));
  }

  /// Plan metadata without the image bytes, keyed by space; one space when spaceId is given.
  private static async Task<Dictionary<Guid, SpacePlan>> LoadPlansAsync(
    VmsCfgDbContext db, Guid? spaceId, CancellationToken ct)
  {
    var rows = await db.SpacePlans.AsNoTracking()
      .Where(p => spaceId == null || p.SpaceId == spaceId)
      .Select(p => new DbSpacePlan
      {
        SpaceId = p.SpaceId, Mime = p.Mime, WidthPx = p.WidthPx, HeightPx = p.HeightPx,
        TlLat = p.TlLat, TlLon = p.TlLon, TrLat = p.TrLat, TrLon = p.TrLon,
        BlLat = p.BlLat, BlLon = p.BlLon, Etag = p.Etag,
      })
      .ToListAsync(ct);
    return rows.ToDictionary(r => r.SpaceId, ToPlan);
  }

  private static SpacePlan ToPlan(DbSpacePlan p) => new()
  {
    Corners = new PlanCorners
    {
      TopLeft = new GeoPoint { Lat = p.TlLat, Lon = p.TlLon },
      TopRight = new GeoPoint { Lat = p.TrLat, Lon = p.TrLon },
      BottomLeft = new GeoPoint { Lat = p.BlLat, Lon = p.BlLon },
    },
    WidthPx = (uint)p.WidthPx,
    HeightPx = (uint)p.HeightPx,
    Mime = p.Mime,
    Etag = p.Etag,
  };

  private static void SetCorners(DbSpacePlan row, PlanCorners c)
  {
    row.TlLat = c.TopLeft.Lat; row.TlLon = c.TopLeft.Lon;
    row.TrLat = c.TopRight.Lat; row.TrLon = c.TopRight.Lon;
    row.BlLat = c.BottomLeft.Lat; row.BlLon = c.BottomLeft.Lon;
  }

  private static Space ToProto(DbSpace s, SpacePlan? plan)
  {
    var space = new Space
    {
      Id = s.Id.ToString(),
      ParentId = s.ParentId?.ToString() ?? string.Empty,
      Name = s.Name,
      Kind = s.Kind == SpaceKinds.Map ? SpaceKind.Map : SpaceKind.Plan,
      RealCoordinates = s.RealCoordinates,
      Ordinal = s.Ordinal,
      ElevationM = s.ElevationM,
      View = new GeoBounds
      {
        SouthWest = new GeoPoint { Lat = s.ViewSouth, Lon = s.ViewWest },
        NorthEast = new GeoPoint { Lat = s.ViewNorth, Lon = s.ViewEast },
      },
    };
    if (s.Footprint is not null)
    {
      var points = JsonSerializer.Deserialize<double[][]>(s.Footprint) ?? [];
      space.Footprint.AddRange(points.Select(p => new GeoPoint { Lat = p[0], Lon = p[1] }));
    }
    if (plan is not null)
      space.Plan = plan;
    return space;
  }

  private static CameraPlacement ToProto(DbCameraPlacement p) => new()
  {
    CameraId = p.CameraId,
    SpaceId = p.SpaceId.ToString(),
    IsReference = p.IsReference,
    Lat = p.Lat,
    Lon = p.Lon,
    HeightM = p.HeightM,
    HeadingDeg = p.HeadingDeg,
    HfovDeg = p.HfovDeg,
    RangeM = p.RangeM,
  };

  private static Landmark ToProto(DbLandmark m) => new()
  {
    Id = m.Id.ToString(),
    SpaceId = m.SpaceId.ToString(),
    Name = m.Name,
    Lat = m.Lat,
    Lon = m.Lon,
    HeightM = m.HeightM,
  };

  internal static Guid ParseId(string raw, string what) =>
    Guid.TryParse(raw, out var id) ? id : throw new SpatialConfigException($"'{raw}' is not a valid {what} id");

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
      throw new SpatialConfigException("The default view must have its south-west corner below and left of its north-east one");
  }

  private static double NormalizeBearing(double degrees)
  {
    var d = degrees % 360;
    return d < 0 ? d + 360 : d;
  }
}
