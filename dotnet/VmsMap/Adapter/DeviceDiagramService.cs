using System;
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
    private readonly IDiagramServiceInternal _diagrams;
    private readonly ICameraCatalog _catalog;
    private readonly IGeoService _geo;
    private readonly DeviceDiagramReconciler _reconciler;

    public DeviceDiagramService(
      VmsMapDbContext db,
      IMapService map,
      IDiagramServiceInternal diagrams,
      ICameraCatalog catalog,
      IGeoService geo,
      DeviceDiagramReconciler reconciler)
    {
      _db = db ?? throw new ArgumentNullException(nameof(db));
      _map = map ?? throw new ArgumentNullException(nameof(map));
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

    public async Task<string?> AttachAsync(
      string sourceKind, string sourceId, string? diagramTypeId, string slot,
      DiagramSite? site, CancellationToken ct)
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

      // The match the device's model gives, read once and reused: creating the row and adopting a
      // template into an already blank one ask the same question, and the answer cannot differ
      // inside one transaction.
      DiagramModelSelection? model = null;

      if (row is null)
      {
        // No template named: the device's own model decides. None matching is not a failure — the
        // sheet is blank, and the administrator draws on it.
        var chosen = wantedType;
        if (chosen is null)
        {
          model = await _reconciler.SelectModelTypeAsync(sourceId, ct);
          if (model.TypeId is { } match)
            chosen = Utils.ConvertObjectIdToGuid(match);
        }

        row = await CreateAsync(sourceKind, sourceId, slot, chosen, site, ct);
        if (row is null)
          return null;
      }
      else
      {
        // "Draw it here" when a canvas is named, "keep it with the camera" when none is: either way
        // the node is nested under the camera's picture, and only a named canvas gives it a point.
        await PlaceAsync(row, site, ct);
      }

      if (wantedType is null && row.diagram_type_id is null)
      {
        model ??= await _reconciler.SelectModelTypeAsync(sourceId, ct);
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

      await _reconciler.ReconcilePartsAsync(
        row, sourceId, await _reconciler.ConfiguredPartsAsync(sourceId, ct), ct);

      if (tx is not null)
        await tx.CommitAsync(ct);

      return Utils.ConvertGuidToObjectId(row.object_id);
    }

    /// <summary>
    /// Creates the diagram under the camera's picture and, on a named canvas, draws it: a point of
    /// its own and the paper its template draws. Without a canvas the node is kept with the camera
    /// and nothing is drawn — a diagram is opened from the camera's own link, and a permanent
    /// picture beside it is a place an administrator asked for. The parts are bound afterwards by
    /// reconciliation, because the device may not have declared them yet — a demo installs before
    /// OnvifSim says what it has.
    /// </summary>
    private async Task<DBDeviceDiagram?> CreateAsync(
      string sourceKind, string sourceId, string slot, Guid? typeId, DiagramSite? site,
      CancellationToken ct)
    {
      Guid? parentGuid;
      (double Lat, double Lon)? point = null;

      if (site is null)
      {
        // No canvas named: under the picture the camera really stands on. A device that is not
        // placed anywhere has no such node, and nothing would be its parent.
        parentGuid = await ReferencePictureAsync(sourceId, ct);
        if (parentGuid is null)
          return null;
      }
      else
      {
        var spaceGuid = Utils.ConvertObjectIdToGuid(site.SpaceId);
        if (spaceGuid is null || !await _db.Spaces.AnyAsync(s => s.object_id == spaceGuid, ct))
          return null;

        parentGuid = Utils.ConvertObjectIdToGuid(
          await ParentOnCanvasAsync(sourceId, spaceGuid.Value, ct));
        point = site.Lat is { } lat && site.Lon is { } lon
          ? (Lat: lat, Lon: lon)
          : await BesideCameraAsync(sourceId, spaceGuid.Value, ct);
      }

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
          // The paper is the template's picture; the point below is where the diagram itself stands
          // on the map, and the two are different things.
          geometry = null,
          region_id = null,
        }
      ]);

      if (point is { } at)
      {
        await _geo.CreateGeo([
          new FigureGeoDTO
          {
            id = objectId,
            parent_id = parent,
            name = name,
            geometry = new GeometryCircleDTO(new Geo2DCoordDTO { Lat = at.Lat, Lon = at.Lon }),
          }
        ]);
      }

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
    /// draws. On a named canvas it also stands there, at the point it already stands at unless the
    /// site names one — a reinstall must not move what was dragged, and a point the administrator
    /// clicked is applied even to a diagram already drawn.
    ///
    /// A diagram already drawn on a canvas is the administrator's: opening it from the camera (no
    /// site) leaves the node and its figure alone, because moving the node out from under that
    /// canvas's picture would leave the figure behind in another canvas's coordinates.
    /// </summary>
    private async Task PlaceAsync(DBDeviceDiagram row, DiagramSite? site, CancellationToken ct)
    {
      var marker = await _db.Markers.AsNoTracking()
        .FirstOrDefaultAsync(m => m.id == row.object_id, ct);
      if (marker is null)
        return;

      var drawn = await _db.GeoObjects.AsNoTracking().AnyAsync(g => g.id == row.object_id, ct);
      // A figure already on this canvas keeps its point; one on another canvas says nothing here.
      var drawnHere = false;

      Guid? parentGuid;
      (double Lat, double Lon)? point = null;
      // A point named outright is applied even when the node already stands where it is asked to:
      // the administrator clicked this exact place, and a card that did not move would be the
      // request silently dropped.
      var namedPoint = false;

      if (site is null)
      {
        if (drawn)
          return;

        parentGuid = await ReferencePictureAsync(row.source_id, ct);
        if (parentGuid is null)
          return;
      }
      else
      {
        var spaceGuid = Utils.ConvertObjectIdToGuid(site.SpaceId);
        if (spaceGuid is null || !await _db.Spaces.AnyAsync(s => s.object_id == spaceGuid, ct))
          return;

        parentGuid = Utils.ConvertObjectIdToGuid(
          await ParentOnCanvasAsync(row.source_id, spaceGuid.Value, ct));

        if (site.Lat is { } lat && site.Lon is { } lon)
        {
          point = (Lat: lat, Lon: lon);
          namedPoint = true;
        }
        else if (drawn && await IsDrawnOnCanvasAsync(row.object_id, spaceGuid.Value, ct))
        {
          // Already here: the point it was dragged to is the point it keeps.
          drawnHere = true;
          point = await CurrentPointAsync(row.object_id, ct)
            ?? await BesideCameraAsync(row.source_id, spaceGuid.Value, ct);
        }
        else
        {
          // Not drawn here — on no canvas, or on one whose coordinates are its own.
          point = await BesideCameraAsync(row.source_id, spaceGuid.Value, ct);
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
    /// Where a diagram hangs on a canvas: under the picture of the camera it draws, when the camera
    /// is drawn there, and under the space itself otherwise. A diagram is a map object and belongs
    /// with the device's picture on the plan; the camera's own node under the server is hardware.
    /// </summary>
    private async Task<string> ParentOnCanvasAsync(string cameraId, Guid spaceId, CancellationToken ct)
    {
      var picture = await PictureAsync(cameraId, spaceId, ct);
      return Utils.ConvertGuidToObjectId(picture ?? spaceId);
    }

    /// The camera's picture on this canvas, when the camera is drawn there.
    private Task<Guid?> PictureAsync(string cameraId, Guid spaceId, CancellationToken ct) =>
      (from p in _db.CameraPlacements
       join m in _db.Markers on p.object_id equals m.id
       where p.camera_id == cameraId && m.parent_id == spaceId
       select (Guid?)p.object_id).FirstOrDefaultAsync(ct);

    /// The picture the camera really stands on: its reference placement. A diagram kept with the
    /// camera without a canvas of its own hangs under it, and that is the one the reader arrives at
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
    /// Whether the diagram's figure is drawn on this canvas. Its coordinates are the canvas's own,
    /// so only a figure standing here says where the card belongs here.
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
