using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;
using VmsMap.Clients;
using VmsMap.Db;
using VmsMap.Domain;

namespace VmsMap.Adapter
{
  /// <summary>
  /// The device's diagram as a map object: where it stands and when one is made.
  ///
  /// The rule that decides what the diagram says — its type and the replicas of the parts the
  /// operator configured — lives in <see cref="DeviceDiagramReconciler"/>, a step this service
  /// runs inside its own transaction and locks. A part that is not configured has no replica and no
  /// state, and the region stays as the template drew it — a template describes the whole model, not
  /// one installation of it. A replica made by hand in the same diagram is not in
  /// <c>device_diagram_parts</c> and is never moved or removed.
  ///
  /// A device has one canonical diagram (per slot) and as many representations of it as canvases
  /// ask for. The canonical is the diagram's own card on a space; a representation stands on a
  /// diagram's canvas and opens on the diagram it represents (see <c>DiagramRepresentations</c>).
  /// </summary>
  /// <remarks>
  /// One transaction and one advisory lock per device: a snapshot and a configuration change
  /// arrive independently, and two runs that both find no diagram would otherwise both create one.
  /// The scenario owns both — reconciliation and placement only write what it already serialised.
  /// </remarks>
  internal sealed class DeviceDiagramService : IDeviceDiagramService
  {
    private readonly VmsMapDbContext _db;
    private readonly IMapService _map;
    private readonly IMapUpdateService _updates;
    private readonly IDiagramServiceInternal _diagrams;
    private readonly ICameraCatalog _catalog;
    private readonly IGeoService _geo;
    private readonly DeviceDiagramReconciler _reconciler;

    public DeviceDiagramService(
      VmsMapDbContext db,
      IMapService map,
      IMapUpdateService updates,
      IDiagramServiceInternal diagrams,
      ICameraCatalog catalog,
      IGeoService geo,
      DeviceDiagramReconciler reconciler)
    {
      _db = db ?? throw new ArgumentNullException(nameof(db));
      _map = map ?? throw new ArgumentNullException(nameof(map));
      _updates = updates ?? throw new ArgumentNullException(nameof(updates));
      _diagrams = diagrams ?? throw new ArgumentNullException(nameof(diagrams));
      _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
      _geo = geo ?? throw new ArgumentNullException(nameof(geo));
      _reconciler = reconciler ?? throw new ArgumentNullException(nameof(reconciler));
    }

    public async Task ReconcileDeviceAsync(
      string sourceKind, string sourceId, string slot, CancellationToken ct)
    {
      if (!IsSupported(sourceKind, sourceId))
        return;

      // The sweep runs this for every camera on every snapshot, and almost always finds nothing to
      // do. The map's locks are transitive and global: taking them for a read that changes nothing
      // would serialise the whole deployment against itself. So the settled case is decided
      // without a transaction, and only a real change pays for the locks.
      if (await _reconciler.IsSettledAsync(sourceKind, sourceId, slot, ct))
        return;

      await using var tx = await OwnTransactionAsync(ct);

      // The same order every writer of the map's rows takes: the camera-state and alarm locks
      // first, then this device's own. Taking the device lock first and reaching camera-state
      // inside a write is the other half of a deadlock against a camera removal, which holds
      // camera-state while it deletes the very rows this would write.
      await MapLocks.AcquireAllAsync(_db, ct);
      await MapLocks.AcquireDeviceDiagramAsync(_db, sourceKind, sourceId, ct);

      await _reconciler.ReconcileAsync(sourceKind, sourceId, slot, ct);

      if (tx is not null)
        await tx.CommitAsync(ct);
    }

    public async Task<string?> PlaceDeviceDiagramAsync(
      string sourceKind, string sourceId, string? diagramTypeId, string slot,
      DiagramTarget? target, CancellationToken ct)
    {
      if (!IsSupported(sourceKind, sourceId))
        return null;

      var wantedType = string.IsNullOrWhiteSpace(diagramTypeId)
        ? null
        : Utils.ConvertObjectIdToGuid(diagramTypeId);

      // A template that is gone is not a template: the diagram is made without one rather than
      // pointing at nothing.
      if (wantedType is { } named && !await _db.DiagramTypes.AnyAsync(t => t.id == named, ct))
        return null;

      await using var tx = await OwnTransactionAsync(ct);
      await MapLocks.AcquireAllAsync(_db, ct);
      await MapLocks.AcquireDeviceDiagramAsync(_db, sourceKind, sourceId, ct);

      var row = await _db.DeviceDiagrams.FirstOrDefaultAsync(
        d => d.source_kind == sourceKind && d.source_id == sourceId && d.slot == slot, ct);

      // The canvas is read before anything is written: a request naming a place the map cannot give
      // must not leave a diagram behind, and the cycle it may close and the region it asks for are
      // properties of rows that already exist.
      var canvas = target is null ? null : await ResolveCanvasAsync(target, ct);
      Guid? shownAlready = null;
      if (canvas is { IsSpace: false } drawnOn)
      {
        if (row is not null)
        {
          await EnsureCanvasOutsideDiagramAsync(row.object_id, drawnOn.Id, ct);
          // A representation already standing here is the answer, not a region that is taken: the
          // same diagram asked for the same region again is the same copy.
          shownAlready = await FindRepresentationAsync(
            drawnOn.Id, row.object_id, target!.RegionId, ct);
        }

        if (shownAlready is null)
          await EnsureRegionIsOfferedAsync(drawnOn, target!, ct);
      }

      row ??= await CreateCanonicalAsync(sourceKind, sourceId, slot, wantedType, canvas, ct);
      if (row is null)
        return null;

      if (wantedType is null && row.diagram_type_id is null)
      {
        var model = await _reconciler.SelectModelTypeAsync(sourceId, ct);
        if (model.TypeId is { } matched
            && Utils.ConvertObjectIdToGuid(matched) is { } matchedId)
        {
          // A sheet left blank takes its model's template once there is one: the administrator
          // asked for the device's diagram, not for a permanently empty one.
          await _reconciler.SetTypeAsync(row, matchedId, ct);
        }
      }

      if (wantedType is { } type && row.diagram_type_id != type)
      {
        // The administrator's choice replaces whatever the template said before; the replicas are
        // rebuilt below against the new rules.
        await _reconciler.SetTypeAsync(row, type, ct);
        row.auto_created = false;
      }

      var placedId = row.object_id;
      if (canvas is { IsSpace: false } shown)
        // A diagram's canvas holds a representation of the diagram, not the diagram itself.
        placedId = await PlaceRepresentationAsync(row, shown.Id, target!, shownAlready, ct);
      else
        await PlaceOnSpaceAsync(row, canvas?.Id, target?.Lat, target?.Lon, ct);

      await _reconciler.ReconcilePartsAsync(
        row, sourceId, await _reconciler.ConfiguredPartsAsync(sourceId, ct), ct);

      if (tx is not null)
        await tx.CommitAsync(ct);

      return Utils.ConvertGuidToObjectId(placedId);
    }

    private sealed record Canvas(Guid Id, bool IsSpace);

    /// <summary>
    /// What kind of canvas the request names, and whether the target it carries fits that kind. A
    /// space takes a point; a diagram's canvas takes a region of its type or a rectangle of its
    /// own. The request-level matrix is also checked in the gRPC layer, where a REST client's
    /// mistakes are told apart from a client that names a canvas that does not exist.
    /// </summary>
    private async Task<Canvas> ResolveCanvasAsync(DiagramTarget target, CancellationToken ct)
    {
      if (Utils.ConvertObjectIdToGuid(target.CanvasId) is not { } id)
        throw new DeviceDiagramException("The canvas does not exist");

      if (await _db.Spaces.AsNoTracking().AnyAsync(s => s.object_id == id, ct))
      {
        if (target.RegionId is not null || target.Geometry is not null)
          throw new DeviceDiagramException(
            "A space takes a point; a region or a rectangle places a diagram on a diagram's canvas");
        return new Canvas(id, true);
      }

      if (!await _db.Diagrams.AsNoTracking().AnyAsync(d => d.id == id, ct))
        throw new DeviceDiagramException("The canvas does not exist");

      if (target.Lat is not null || target.Lon is not null)
        throw new DeviceDiagramException(
          "A diagram's canvas takes a region or a rectangle, not a point");
      if (target.RegionId is not null && target.Geometry is not null)
        throw new DeviceDiagramException(
          "A representation stands in a region or at a rectangle, not both");

      return new Canvas(id, false);
    }

    /// <summary>
    /// A diagram cannot be shown on itself or on anything drawn inside it: the representation would
    /// hang under the diagram it represents, and every walk of the tree — the alarm aggregation, the
    /// rights — would descend into the copy that shows what it walks through. Refused before the
    /// rows are written; the hierarchy's own cycle check covers moves only.
    /// </summary>
    private async Task EnsureCanvasOutsideDiagramAsync(
      Guid diagramId, Guid canvasId, CancellationToken ct)
    {
      var current = canvasId;
      var seen = new HashSet<Guid>();
      while (seen.Add(current))
      {
        if (current == diagramId)
          throw new DeviceDiagramException("A diagram cannot be shown on its own canvas");

        var parent = await _db.Markers.AsNoTracking()
          .Where(m => m.id == current)
          .Select(m => m.parent_id)
          .FirstOrDefaultAsync(ct);
        if (parent is not { } next)
          return;
        current = next;
      }
    }

    /// <summary>
    /// The canonical diagram's node under the camera's picture, made from the template named or the
    /// one the model names — a blank sheet when none does — and nothing else: where it stands is
    /// the caller's next step. A device that is not placed at all has no picture to hang a diagram
    /// under, and answers null.
    /// </summary>
    private async Task<DBDeviceDiagram?> CreateCanonicalAsync(
      string sourceKind, string sourceId, string slot, Guid? typeId, Canvas? canvas,
      CancellationToken ct)
    {
      Guid? parentGuid = canvas is { IsSpace: true } space
        ? Utils.ConvertObjectIdToGuid(await ParentOnCanvasAsync(sourceId, space.Id, ct))
        : await ReferencePictureAsync(sourceId, ct);
      if (parentGuid is null)
        return null;

      var parent = Utils.ConvertGuidToObjectId(parentGuid);

      var type = typeId is { } id
        ? await _db.DiagramTypes.AsNoTracking().FirstOrDefaultAsync(t => t.id == id, ct)
        : null;

      var camera = await _catalog.GetAsync(sourceId, ct);
      var objectId = DeviceObjectId.ForDeviceDiagram(sourceKind, sourceId, slot);
      var name = $"{camera?.Name ?? sourceId} / {type?.name ?? "Diagram"}";

      await _map.UpdateHierarchyAsync([
        new BaseMarkerDTO { id = objectId, parent_id = parent, name = name }
      ]);

      await _diagrams.UpdateListAsync([
        new DiagramDTO
        {
          id = objectId,
          dgr_type = typeId is { } drawn ? Utils.ConvertGuidToObjectId(drawn) : null,
          // The paper is the template's picture; the point the caller adds below is where the
          // diagram itself stands on the map, and the two are different things.
          geometry = null,
          region_id = null,
        }
      ]);

      var row = new DBDeviceDiagram
      {
        object_id = Utils.ConvertObjectIdToGuid(objectId)!.Value,
        source_kind = sourceKind,
        source_id = sourceId,
        slot = slot,
        diagram_type_id = typeId,
        // The administrator asked for it: an automatic one would mean a diagram nobody placed.
        auto_created = false,
      };
      _db.DeviceDiagrams.Add(row);
      await _db.SaveChangesAsync(ct);
      return row;
    }

    /// <summary>
    /// Keeps an existing diagram with its camera: the node moves under the picture of the camera it
    /// draws. On a named space it also stands there, at the point it already stands at unless the
    /// request names one — a reinstall must not move what was dragged, and a point the administrator
    /// clicked is applied even to a diagram already drawn.
    ///
    /// A diagram already drawn on a space is the administrator's: opening it from the camera (no
    /// space) leaves the node and its figure alone, because moving the node out from under that
    /// space's picture would leave the figure behind in another space's coordinates.
    /// </summary>
    private async Task PlaceOnSpaceAsync(
      DBDeviceDiagram row, Guid? spaceId, double? lat, double? lon, CancellationToken ct)
    {
      var marker = await _db.Markers.AsNoTracking()
        .FirstOrDefaultAsync(m => m.id == row.object_id, ct);
      if (marker is null)
        return;

      var drawn = await _db.GeoObjects.AsNoTracking().AnyAsync(g => g.id == row.object_id, ct);
      // A figure already on this space keeps its point; one on another canvas says nothing here.
      var drawnHere = false;

      Guid? parentGuid;
      (double Lat, double Lon)? point = null;
      // A point named outright is applied even when the node already stands where it is asked to:
      // the administrator clicked this exact place, and a card that did not move would be the
      // request silently dropped.
      var namedPoint = false;

      if (spaceId is not { } space)
      {
        if (drawn)
          return;

        parentGuid = await ReferencePictureAsync(row.source_id, ct);
        if (parentGuid is null)
          return;
      }
      else
      {
        if (!await _db.Spaces.AnyAsync(s => s.object_id == space, ct))
          return;

        parentGuid = Utils.ConvertObjectIdToGuid(
          await ParentOnCanvasAsync(row.source_id, space, ct));

        if (lat is { } atLat && lon is { } atLon)
        {
          point = (Lat: atLat, Lon: atLon);
          namedPoint = true;
        }
        else if (drawn && await IsDrawnOnCanvasAsync(row.object_id, space, ct))
        {
          // Already here: the point it was dragged to is the point it keeps.
          drawnHere = true;
          point = await CurrentPointAsync(row.object_id, ct)
            ?? await BesideCameraAsync(row.source_id, space, ct);
        }
        else
        {
          // Not drawn here — on no canvas, or on one whose coordinates are its own.
          point = await BesideCameraAsync(row.source_id, space, ct);
        }
      }

      var objectId = Utils.ConvertGuidToObjectId(row.object_id);
      var parent = Utils.ConvertGuidToObjectId(parentGuid);

      if (marker.parent_id != parentGuid)
      {
        await _map.UpdateHierarchyAsync([
          new BaseMarkerDTO { id = objectId, parent_id = parent, name = marker.name }
        ]);
      }

      if (point is { } at && (namedPoint || !drawnHere))
      {
        await _geo.CreateGeo([
          new FigureGeoDTO
          {
            id = objectId,
            parent_id = parent,
            name = marker.name,
            geometry = new GeometryCircleDTO(new Geo2DCoordDTO { Lat = at.Lat, Lon = at.Lon }),
          }
        ]);
      }
    }

    /// <summary>
    /// Whether the canvas's type offers the region the request names: the key has to be one of its
    /// regions, and it must not be the slot a part of the template is drawn in — the reconciler's
    /// replica stands there, and a second node would hide it or be hidden by it. A region is a slot
    /// otherwise, so a node already standing in it is refused too.
    /// </summary>
    private async Task EnsureRegionIsOfferedAsync(Canvas canvas, DiagramTarget target, CancellationToken ct)
    {
      if (target.RegionId is not { } regionKey)
        return;

      var canvasType = await _db.Diagrams.AsNoTracking()
        .Where(d => d.id == canvas.Id)
        .Select(d => d.dgr_type)
        .FirstOrDefaultAsync(ct);

      var known = canvasType is { } typeId && await _db.DiagramTypeRegions.AsNoTracking()
        .AnyAsync(r => r.diagram_type_id == typeId && r.region_key == regionKey, ct);
      if (!known)
        throw new DeviceDiagramException($"The canvas has no region \"{regionKey}\"");

      var bound = await _db.DiagramTypeParts.AsNoTracking()
        .AnyAsync(p => p.diagram_type_id == canvasType!.Value && p.region_key == regionKey, ct);
      if (bound)
        throw new DeviceDiagramException(
          $"The region \"{regionKey}\" stands for a part of the canvas's template");

      var occupied = await (from m in _db.Markers.AsNoTracking()
                            join d in _db.Diagrams.AsNoTracking() on m.id equals d.id
                            where m.parent_id == canvas.Id && d.region_id == regionKey
                            select (Guid?)m.id).FirstOrDefaultAsync(ct);
      if (occupied is not null)
        throw new DeviceDiagramException($"The region \"{regionKey}\" already holds a node");
    }

    /// <summary>
    /// The representation the request already has on the canvas: the same diagram in the same
    /// region. Asking again is not a second copy — the region is where it stands, and it is already
    /// there. A rectangle names no region, so a diagram shown at one is not found by this.
    /// </summary>
    private Task<Guid?> FindRepresentationAsync(
      Guid canvasId, Guid diagramId, string? regionKey, CancellationToken ct)
    {
      if (regionKey is null)
        return Task.FromResult<Guid?>(null);

      return (from m in _db.Markers.AsNoTracking()
              join d in _db.Diagrams.AsNoTracking() on m.id equals d.id
              where m.parent_id == canvasId
                    && m.owner_id == diagramId
                    && d.region_id == regionKey
              select (Guid?)m.id).FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// Puts the canonical diagram on a diagram's canvas: a replica whose owner is the diagram, its
    /// own row holding nothing but the place — the region it was given, or the rectangle of its own.
    /// The checks the request can fail on are made before anything is written (see
    /// <see cref="EnsureRegionIsOfferedAsync"/>); what is left here is the reuse of a representation
    /// already standing in that region and the write itself.
    ///
    /// The diagram's own card on a space is taken away: a diagram shown on a canvas is not also a
    /// point beside a camera.
    /// </summary>
    private async Task<Guid> PlaceRepresentationAsync(
      DBDeviceDiagram row, Guid canvasId, DiagramTarget target, Guid? shownAlready, CancellationToken ct)
    {
      // The diagram stands on a canvas now: the point beside the camera it may have had is not a
      // second card, it is the same diagram asked to be shown somewhere else.
      await _geo.RemoveAsync([Utils.ConvertGuidToObjectId(row.object_id)!]);

      if (shownAlready is { } found)
        return found;

      var name = (await _db.Markers.AsNoTracking()
        .Where(m => m.id == row.object_id)
        .Select(m => m.name)
        .FirstOrDefaultAsync(ct)) ?? row.source_id;

      var canvas = Utils.ConvertGuidToObjectId(canvasId);
      var created = await _updates.UpdateObjects([
        new BaseMarkerDTO
        {
          parent_id = canvas,
          owner_id = Utils.ConvertGuidToObjectId(row.object_id),
          name = name,
        }
      ]);

      var replicaId = created[0].id!;
      await _diagrams.UpdateListAsync([
        new DiagramDTO { id = replicaId, region_id = target.RegionId, geometry = target.Geometry }
      ]);

      return Utils.ConvertObjectIdToGuid(replicaId)!.Value;
    }

    /// <summary>
    /// Where a diagram hangs on a space: under the picture of the camera it draws, when the camera
    /// is drawn there, and under the space itself otherwise. A diagram is a map object and belongs
    /// with the device's picture on the plan; the camera's own node under the server is hardware.
    /// </summary>
    private async Task<string> ParentOnCanvasAsync(string cameraId, Guid spaceId, CancellationToken ct)
    {
      var picture = await PictureAsync(cameraId, spaceId, ct);
      return Utils.ConvertGuidToObjectId(picture ?? spaceId);
    }

    /// The camera's picture on this space, when the camera is drawn there.
    private Task<Guid?> PictureAsync(string cameraId, Guid spaceId, CancellationToken ct) =>
      (from p in _db.CameraPlacements
       join m in _db.Markers on p.object_id equals m.id
       where p.camera_id == cameraId && m.parent_id == spaceId
       select (Guid?)p.object_id).FirstOrDefaultAsync(ct);

    /// The picture the camera really stands on: its reference placement. A diagram kept with the
    /// camera without a space of its own hangs under it, and that is the one the reader arrives at
    /// from anywhere in the deployment.
    private Task<Guid?> ReferencePictureAsync(string cameraId, CancellationToken ct) =>
      (from p in _db.CameraPlacements
       join m in _db.Markers on p.object_id equals m.id
       where p.camera_id == cameraId && p.is_reference
       select (Guid?)p.object_id).FirstOrDefaultAsync(ct);

    /// Where the diagram stands now, when it already has a figure of its own.
    private async Task<(double Lat, double Lon)?> CurrentPointAsync(Guid objectId, CancellationToken ct)
    {
      var geo = await _db.GeoObjects.AsNoTracking()
        .FirstOrDefaultAsync(g => g.id == objectId, ct);
      return geo?.figure is Point point ? (point.Y, point.X) : null;
    }

    /// <summary>
    /// Whether the diagram's figure is drawn on this space. Its coordinates are the space's own, so
    /// only a figure standing here says where the card belongs here.
    /// </summary>
    private Task<bool> IsDrawnOnCanvasAsync(Guid objectId, Guid spaceId, CancellationToken ct) =>
      _db.GeoObjects.AsNoTracking().AnyAsync(g => g.id == objectId && g.canvas_id == spaceId, ct);

    /// A few metres off the camera: on its marker would hide one behind the other. The camera's own
    /// picture in that space says where; a space it is not drawn in yet falls back to the middle of
    /// the view, because a diagram without a point cannot be drawn at all.
    private async Task<(double Lat, double Lon)> BesideCameraAsync(
      string cameraId, Guid spaceId, CancellationToken ct)
    {
      if (await PictureAsync(cameraId, spaceId, ct) is { } placement)
      {
        var geo = await _db.GeoObjects.AsNoTracking()
          .FirstOrDefaultAsync(g => g.id == placement, ct);
        if (geo?.figure is Point point)
          return Offset(point.Y, point.X);
      }

      var space = await _db.Spaces.AsNoTracking()
        .FirstOrDefaultAsync(s => s.object_id == spaceId, ct);
      return space is null
        ? (0, 0)
        : ((space.view_south + space.view_north) / 2, (space.view_west + space.view_east) / 2);
    }

    /// Four metres east and north: a fixed offset, because the diagram's exact place is the
    /// administrator's to drag, and a stored preference would be a setting for one drag.
    private static (double Lat, double Lon) Offset(double lat, double lon)
    {
      const double metres = 4;
      var dLat = metres / 111_320.0;
      var dLon = metres / (111_320.0 * Math.Cos(lat * Math.PI / 180));
      return (lat + dLat, lon + dLon);
    }

    private bool IsSupported(string sourceKind, string sourceId) =>
      string.Equals(sourceKind, DeviceObjectId.CameraSource, StringComparison.Ordinal)
      && !string.IsNullOrWhiteSpace(sourceId);

    private Task<Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction?> OwnTransactionAsync(
      CancellationToken ct) =>
      _db.Database.IsRelational() && _db.Database.CurrentTransaction is null
        ? _db.Database.BeginTransactionAsync(ct)
        : Task.FromResult<Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction?>(null);
  }
}
