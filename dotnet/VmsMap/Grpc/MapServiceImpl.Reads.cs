using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using VmsMap.Db;
using VmsMap.Domain;
using MapProto = Vms.Map;

namespace VmsMap.Grpc
{
  // The read half of MapService. VmsMap owns the tree read model as well as the write one, so
  // every answer is built here from the repositories rather than read back over another service.
  // Reads that can return more than one node ask IRightsService first: a node the caller has no
  // `view` on never leaves the process.
  public partial class MapServiceImpl
  {
    // ── Objects ───────────────────────────────────────────────────────────────────────────────

    public override async Task<MapProto.GetByParentResponse> GetByParent(
      MapProto.GetByParentRequest request, ServerCallContext context)
    {
      var ct = context.CancellationToken;
      var caller = ToCaller(request.Caller);
      var parentId = request.ParentId ?? string.Empty;

      // Child first from the repository; the browser draws the chain root first.
      var parents = await _mapService.GetByChildIdAsync(parentId);
      parents.Reverse();

      var children = await _mapService.GetByParentIdAsync(
        parentId, request.StartId ?? string.Empty, request.EndId ?? string.Empty,
        request.Count > 0 ? request.Count : 10_000);

      var candidates = children.Keys
        .Concat(parents.Select(p => p.id).Where(id => !string.IsNullOrEmpty(id))!)
        .Distinct(StringComparer.Ordinal)
        .ToList();
      var visible = await _rightsService.VisibleAsync(candidates, caller, ct);

      children = children
        .Where(kv => visible.Contains(kv.Key))
        .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
      parents = parents.Where(p => p.id is not null && visible.Contains(p.id)).ToList();

      var top = await _mapService.GetTopChildren(children.Keys.ToList());
      var cameras = await CameraIdsAsync(children.Keys, ct);
      var kinds = await NodeKindLookup.ResolveAsync(_db, candidates, ct);

      var guids = children.Keys
        .Select(Utils.ConvertObjectIdToGuid)
        .Where(g => g.HasValue)
        .Select(g => g!.Value)
        .ToList();

      return new MapProto.GetByParentResponse
      {
        ParentId = parentId,
        Parents =
        {
          parents.Select(p => ToMarker(
            p, false, null, kinds.GetValueOrDefault(p.id ?? string.Empty, NodeKind.Object)))
        },
        Children =
        {
          children.Select(kv => ToMarker(
            kv.Value, top.ContainsKey(kv.Key), cameras.GetValueOrDefault(kv.Key),
            kinds.GetValueOrDefault(kv.Key, NodeKind.Object)))
        },
        StartId = guids.Count > 0 ? Utils.ConvertGuidToObjectId(guids.Min()) : string.Empty,
        EndId = guids.Count > 0 ? Utils.ConvertGuidToObjectId(guids.Max()) : string.Empty,
      };
    }

    public override async Task<MapProto.Figures> GetByIds(
      MapProto.GetByIdsRequest request, ServerCallContext context)
    {
      var ids = await VisibleAsync(request.Ids, request.Caller, context.CancellationToken);
      return await FiguresAsync(ids, context.CancellationToken);
    }

    public override async Task<MapProto.MarkerList> GetMarkersByIds(
      MapProto.GetByIdsRequest request, ServerCallContext context)
    {
      var ct = context.CancellationToken;
      var ids = await VisibleAsync(request.Ids, request.Caller, ct);

      var markers = await _mapService.GetAsync(ids.ToList());
      var top = await _mapService.GetTopChildren(ids.ToList());
      var cameras = await CameraIdsAsync(ids, ct);
      var kinds = await NodeKindLookup.ResolveAsync(_db, ids, ct);

      return new MapProto.MarkerList
      {
        Markers =
        {
          markers.Values.Select(m => ToMarker(
            m, top.ContainsKey(m.id ?? string.Empty),
            cameras.GetValueOrDefault(m.id ?? string.Empty),
            kinds.GetValueOrDefault(m.id ?? string.Empty, NodeKind.Object)))
        }
      };
    }

    public override async Task<MapProto.Figures> GetByBox(
      MapProto.GetByBoxRequest request, ServerCallContext context)
    {
      var ct = context.CancellationToken;
      var caller = ToCaller(request.Caller);
      var box = ToBoxDto(request.Box);

      var ids = new List<string>();
      if (request.PropertyFilter.Count > 0)
      {
        var props = await _mapService.GetPropByValuesAsync(
          new ObjPropsSearchDTO
          {
            props = request.PropertyFilter
              .Select(p => new KeyValueDTO { prop_name = p.PropName, str_val = p.StrVal })
              .ToList(),
          },
          null, 1, 1000);

        ids.AddRange(props.Select(p => p.id).Where(id => !string.IsNullOrEmpty(id))!);
        if (ids.Count > 0)
        {
          box.ids ??= [];
          box.ids.AddRange(ids);
        }
      }

      var geo = await _geoService.GetGeoAsync(box);
      var visible = await _rightsService.VisibleAsync(geo.Keys.ToList(), caller, ct);
      geo = geo
        .Where(kv => visible.Contains(kv.Key))
        .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);

      var figures = await _mapService.GetFigures(geo);

      // A canvas is drawn whole: the client shows the composition of one space and paging it into
      // centroids would drop the very containers it exists to draw. A rectangle keeps the cut.
      if (!string.IsNullOrEmpty(box.canvas_id) || figures.figs.Count < 1000)
        return await ToProtoFiguresAsync(figures, geo.Keys, ct);

      var zooms = await _levelService.GetAllZooms();
      figures.figs.Sort(new ZoomComparer(zooms));

      var raw = figures.figs.Take(500).ToList();
      var rest = figures.figs.Skip(500).ToList();
      var centroids = KMeans.GetCentroids(rest, Math.Max(100, 1 + rest.Count / 1000));
      centroids.figs.AddRange(raw);

      return await ToProtoFiguresAsync(centroids, geo.Keys, ct);
    }

    public override async Task<MapProto.MarkerList> GetByName(
      MapProto.GetByNameRequest request, ServerCallContext context)
    {
      var ct = context.CancellationToken;
      var caller = ToCaller(request.Caller);

      var markers = await _mapService.GetByNameAsync(request.Name ?? string.Empty);
      var visible = await _rightsService.VisibleAsync(markers.Keys, caller, ct);
      markers = markers
        .Where(kv => visible.Contains(kv.Key))
        .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);

      var top = await _mapService.GetTopChildren(markers.Keys.ToList());
      var cameras = await CameraIdsAsync(markers.Keys, ct);
      var kinds = await NodeKindLookup.ResolveAsync(_db, markers.Keys, ct);

      return new MapProto.MarkerList
      {
        Markers =
        {
          markers.Select(kv => ToMarker(
            kv.Value, top.ContainsKey(kv.Key), cameras.GetValueOrDefault(kv.Key),
            kinds.GetValueOrDefault(kv.Key, NodeKind.Object)))
        }
      };
    }

    public override async Task<MapProto.Figures> GetByParams(
      MapProto.GetByParamsRequest request, ServerCallContext context)
    {
      var ct = context.CancellationToken;

      var props = await _mapService.GetPropByValuesAsync(
        new ObjPropsSearchDTO
        {
          props = request.Props
            .Select(p => new KeyValueDTO { prop_name = p.PropName, str_val = p.StrVal })
            .ToList(),
        },
        request.StartId ?? string.Empty,
        request.Forward,
        request.Count > 0 ? request.Count : 100);

      var ids = props.Select(p => p.id).Where(id => !string.IsNullOrEmpty(id)).ToList();
      var visible = await VisibleAsync(ids, request.Caller, ct);
      return await FiguresAsync(visible, ct);
    }

    public override async Task<MapProto.ObjProps> GetObjProps(
      MapProto.GetObjPropsRequest request, ServerCallContext context)
    {
      var ct = context.CancellationToken;
      var caller = ToCaller(request.Caller);

      if (string.IsNullOrWhiteSpace(request.Id) ||
          !await _rightsService.IsAllowedAsync(request.Id, RightActions.View, caller, ct))
      {
        return new MapProto.ObjProps();
      }

      var marker = await _mapService.GetAsync(request.Id);
      if (marker is null)
        return new MapProto.ObjProps();

      var kinds = await NodeKindLookup.ResolveAsync(_db, [request.Id], ct);
      // The panel that opens on this read replaces the node in the client's store: a hard-coded
      // "no children" here would take the "enter" action away from every container the moment it
      // is selected.
      var top = await _mapService.GetTopChildren([request.Id]);
      var result = new MapProto.ObjProps
      {
        Marker = ToMarker(
          marker, top.ContainsKey(request.Id),
          (await CameraIdsAsync([request.Id], ct)).GetValueOrDefault(request.Id),
          kinds.GetValueOrDefault(request.Id, NodeKind.Object)),
      };

      var props = await _mapService.GetPropAsync(request.Id);
      if (props?.extra_props is { Count: > 0 })
        result.ExtraProps.AddRange(props.extra_props.Select(ToProto));

      var visual = (await _visualService.GetListByIdsAsync([request.Id]))
        .GetValueOrDefault(request.Id);
      if (visual is not null)
        result.Visual = ToProto(visual);

      var geo = await _geoService.GetGeoObjectAsync(request.Id);
      if (!string.IsNullOrEmpty(geo?.zoom_level))
      {
        var level = await _levelService.GetByZoomLevel(geo.zoom_level);
        if (level is not null)
        {
          result.ZoomMin = level.zoom_min;
          result.ZoomMax = level.zoom_max;
        }
      }

      return result;
    }

    public override async Task<MapProto.Marker> CreateReplica(
      MapProto.CreateReplicaRequest request, ServerCallContext context)
    {
      var ct = context.CancellationToken;
      var caller = ToCaller(request.Caller);

      var owner = await _mapService.GetAsync(request.OwnerId);
      if (owner is null)
        throw new RpcException(new Status(StatusCode.InvalidArgument, "The owner node does not exist"));

      if (string.IsNullOrWhiteSpace(request.ParentId))
        throw new RpcException(new Status(StatusCode.InvalidArgument, "A replica needs a parent"));

      if (!await _rightsService.IsAllowedAsync(request.ParentId, RightActions.Edit, caller, ct))
        throw new RpcException(new Status(StatusCode.PermissionDenied, "No edit right on the parent"));

      var dto = new BaseMarkerDTO
      {
        parent_id = request.ParentId,
        owner_id = request.OwnerId,
        name = string.IsNullOrWhiteSpace(request.Name) ? owner.name : request.Name,
      };

      await using var tx = _db.Database.IsRelational()
        ? await _db.Database.BeginTransactionAsync(ct)
        : null;

      await _mapUpdateService.UpdateObjects([dto]);

      var ownerProps = await _mapService.GetPropAsync(request.OwnerId);
      if (ownerProps?.extra_props is { Count: > 0 })
      {
        await _mapService.UpdatePropAsync([
          new ObjPropsDTO { id = dto.id, extra_props = ownerProps.extra_props }
        ]);
      }

      var ownerVisual = (await _visualService.GetListByIdsAsync([request.OwnerId]))
        .GetValueOrDefault(request.OwnerId);
      if (ownerVisual is not null)
      {
        await _visualService.UpdateListAsync([
          ownerVisual with { object_id = dto.id }
        ]);
      }

      if (tx is not null)
        await tx.CommitAsync(ct);

      var kinds = await NodeKindLookup.ResolveAsync(_db, [dto.id!], ct);
      return ToMarker(
        dto, false,
        (await CameraIdsAsync([dto.id!], ct)).GetValueOrDefault(dto.id!),
        kinds.GetValueOrDefault(dto.id!, NodeKind.Object));
    }

    // ── States ────────────────────────────────────────────────────────────────────────────────

    public override async Task<MapProto.ObjectStateList> GetStates(
      MapProto.GetStatesRequest request, ServerCallContext context)
    {
      var ct = context.CancellationToken;
      var ids = await VisibleAsync(request.Ids, request.Caller, ct);
      var states = await StatesForAsync(ids, ct);

      return new MapProto.ObjectStateList
      {
        States =
        {
          ids.Where(states.ContainsKey).Select(id => ToProto(states[id]))
        }
      };
    }

    public override async Task<MapProto.VisualStates> GetVisualStates(
      MapProto.GetStatesRequest request, ServerCallContext context)
    {
      var ct = context.CancellationToken;
      var ids = await VisibleAsync(request.Ids, request.Caller, ct);

      var states = await StatesForAsync(ids, ct);
      var alarms = await AlarmsForAsync(ids, ct);

      var names = states.Values
        .SelectMany(s => s.states ?? [])
        .Distinct(StringComparer.Ordinal)
        .ToList();
      var descriptions = await _stateService.GetStateDescrAsync(names);

      return new MapProto.VisualStates
      {
        AlarmedObjects = { alarms },
        States = { ids.Where(states.ContainsKey).Select(id => ToProto(states[id])) },
        StatesDescr = { descriptions.Select(ToProto) },
      };
    }

    public override async Task<MapProto.AlarmStateList> GetAlarmedStates(
      MapProto.GetStatesRequest request, ServerCallContext context)
    {
      var ct = context.CancellationToken;

      // No ids means the whole map: the operator's alarm list is not a set of ids the browser
      // already knows, and an alarm that started before the page opened is the one that matters.
      if (request.Ids.Count == 0)
      {
        var alarmed = await _db.AlarmStates.AsNoTracking()
          .Where(s => s.alarm || s.children_alarms > 0)
          .ToListAsync(ct);
        var all = alarmed
          .Select(s => Utils.ConvertGuidToObjectId(s.id))
          .ToList();
        var visibleAll = await _rightsService.VisibleAsync(all, ToCaller(request.Caller), ct);

        return new MapProto.AlarmStateList
        {
          States =
          {
            alarmed
              .Where(s => visibleAll.Contains(Utils.ConvertGuidToObjectId(s.id)))
              .Select(s => new MapProto.AlarmState
              {
                Id = Utils.ConvertGuidToObjectId(s.id),
                Alarm = s.alarm,
                ChildrenAlarms = s.children_alarms,
                Version = s.Version,
              })
          }
        };
      }

      var ids = await VisibleAsync(request.Ids, request.Caller, ct);
      return new MapProto.AlarmStateList { States = { await AlarmsForAsync(ids, ct) } };
    }

    public override async Task<MapProto.StateDescriptionList> GetStateDescriptions(
      MapProto.GetStateDescriptionsRequest request, ServerCallContext context)
    {
      var descriptions = await _stateService.GetStateDescrAsync(request.States.ToList());
      return new MapProto.StateDescriptionList { Descriptions = { descriptions.Select(ToProto) } };
    }

    // ── Events ────────────────────────────────────────────────────────────────────────────────

    public override async Task<MapProto.EventList> GetEventsByFilter(
      MapProto.GetEventsByFilterRequest request, ServerCallContext context)
    {
      var ct = context.CancellationToken;

      var filter = new SearchEventFilterDTO
      {
        time_start = request.TimeStart?.ToDateTime(),
        time_end = request.TimeEnd?.ToDateTime(),
        objects = request.Objects.ToList(),
        images_only = request.ImagesOnly,
        param0 = string.IsNullOrEmpty(request.Param0) ? null : request.Param0,
        param1 = string.IsNullOrEmpty(request.Param1) ? null : request.Param1,
        start_id = string.IsNullOrEmpty(request.StartId) ? null : request.StartId,
        count = request.Count,
        sort = string.IsNullOrEmpty(request.SortKey)
          ? null
          : [new SortDTO { key = request.SortKey, order = request.SortOrder }],
      };

      var events = await _eventsService.GetEventsByFilter(filter);

      var caller = ToCaller(request.Caller);
      var objectIds = events
        .Select(e => e.object_id)
        .Where(id => !string.IsNullOrEmpty(id))
        .Distinct(StringComparer.Ordinal)
        .ToList()!;
      var visible = await _rightsService.VisibleAsync(objectIds, caller, ct);

      return new MapProto.EventList
      {
        Events =
        {
          events
            .Where(e => e.object_id is not null && visible.Contains(e.object_id))
            .Select(ToProto)
        }
      };
    }

    // ── Tracks, values and diagrams ──────────────────────────────────────────────────────────

    public override async Task<MapProto.TrackPointList> GetTracksByFilter(
      MapProto.GetTracksByFilterRequest request, ServerCallContext context)
    {
      var ct = context.CancellationToken;

      var filter = new SearchFilterDTO
      {
        time_start = request.TimeStart?.ToDateTime(),
        time_end = request.TimeEnd?.ToDateTime(),
        start_id = string.IsNullOrEmpty(request.StartId) ? null : request.StartId,
        forward = request.Forward,
        count = request.Count,
        property_filter = request.PropertyFilter.Count == 0
          ? null
          : new ObjPropsSearchDTO
          {
            props = request.PropertyFilter
              .Select(p => new KeyValueDTO { prop_name = p.PropName, str_val = p.StrVal })
              .ToList(),
          },
      };

      var tracks = await _trackService.GetTracksByFilter(filter);
      return await ToProtoTracksAsync(tracks, request.Caller, ct);
    }

    public override async Task<MapProto.TrackPointList> GetTracksByBox(
      MapProto.GetTracksByBoxRequest request, ServerCallContext context)
    {
      var ct = context.CancellationToken;

      var box = new BoxTrackDTO
      {
        time_start = request.TimeStart?.ToDateTime(),
        time_end = request.TimeEnd?.ToDateTime(),
        sort = request.Sort,
      };
      CopyBox(ToBoxDto(request.Box), box);

      var tracks = await _trackService.GetTracksByBox(box);
      return await ToProtoTracksAsync(tracks, request.Caller, ct);
    }

    public override async Task<MapProto.ValueList> GetValuesByOwners(
      MapProto.GetValuesByOwnersRequest request, ServerCallContext context)
    {
      var ct = context.CancellationToken;
      var ids = await VisibleAsync(request.Ids, request.Caller, ct);

      var ownersAndViews = await _mapService.GetOwnersAndViewsAsync(ids.ToList());
      var owners = ownersAndViews
        .Where(kv => string.IsNullOrEmpty(kv.Value.owner_id))
        .Select(kv => kv.Key)
        .ToList();

      var values = await _valuesService.GetListByOwnersAsync(owners);
      var result = new MapProto.ValueList();

      foreach (var id in ids)
      {
        if (!ownersAndViews.TryGetValue(id, out var view))
          continue;

        var ownerId = string.IsNullOrEmpty(view.owner_id) ? id : view.owner_id;
        var matching = values.Values.Where(v => v.owner_id == ownerId).ToList();

        if (string.IsNullOrEmpty(view.owner_id))
        {
          result.Values.AddRange(matching.Select(ToProto));
        }
        else
        {
          // A replica shows its owner's values under its own id.
          result.Values.AddRange(matching.Select(v => ToProto(v with { id = null, owner_id = id })));
        }
      }

      return result;
    }

    public override async Task<MapProto.DiagramContent> GetDiagramContent(
      MapProto.GetDiagramContentRequest request, ServerCallContext context)
    {
      var ct = context.CancellationToken;
      var id = request.DiagramId ?? string.Empty;
      var content = new MapProto.DiagramContent
      {
        DiagramId = id,
        Depth = request.Depth,
      };

      if (string.IsNullOrWhiteSpace(id) ||
          !await _rightsService.IsAllowedAsync(id, RightActions.View, ToCaller(request.Caller), ct))
      {
        return content;
      }

      var marker = await _mapService.GetAsync(id);
      if (marker is null)
        return content;

      var markers = new Dictionary<string, BaseMarkerDTO>(StringComparer.Ordinal) { [id] = marker };
      if (!string.IsNullOrEmpty(marker.parent_id))
        markers[marker.parent_id] = (await _mapService.GetAsync(marker.parent_id))!;

      if (request.Depth > 0)
      {
        var children = await _mapService.GetByParentIdAsync(id, null, null, 1000);
        foreach (var (childId, child) in children)
          markers[childId] = child;

        for (var level = 1; level < request.Depth && children.Count > 0; level++)
        {
          children = await _mapService.GetByParentIdsAsync(
            children.Keys.ToList(), null, null, 1000);
          foreach (var (childId, child) in children)
            markers[childId] = child;
        }
      }

      var visible = await _rightsService.VisibleAsync(
        markers.Keys, ToCaller(request.Caller), ct);
      markers = markers
        .Where(kv => visible.Contains(kv.Key))
        .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);

      var diagrams = await _diagramService.GetListByIdsAsync(markers.Keys.ToList());
      var typeIds = diagrams.Values
        .Select(d => d.dgr_type)
        .Where(t => !string.IsNullOrEmpty(t))
        .Distinct(StringComparer.Ordinal)
        .ToList()!;
      var types = await _diagramTypeService.GetListByTypeIdsAsync(typeIds);

      var parents = await _mapService.GetByChildIdAsync(id);
      parents.Reverse();

      // The parents are the chain the browser draws as breadcrumbs; some of them are not among
      // the collected markers, so they are added to the kind and visual lookups explicitly.
      var allIds = markers.Keys
        .Concat(parents.Select(p => p.id).Where(p => !string.IsNullOrEmpty(p))!)
        .Distinct(StringComparer.Ordinal)
        .ToList();
      var kinds = await NodeKindLookup.ResolveAsync(_db, allIds, ct);
      var visuals = await _visualService.GetListByIdsAsync(allIds);

      content.Children.AddRange(markers.Values
        .Where(m => m.id != id)
        .Select(m => ToMarker(
          m, false, null,
          kinds.GetValueOrDefault(m.id ?? string.Empty, NodeKind.Object))));
      content.Content.AddRange(diagrams.Values.Select(ToProto));
      content.DgrTypes.AddRange(types.Values.Select(ToProto));
      content.Parents.AddRange(parents.Select(p => ToMarker(
        p, false, null, kinds.GetValueOrDefault(p.id ?? string.Empty, NodeKind.Object))));
      content.Visuals.AddRange(visuals.Values.Select(ToProto));

      return content;
    }

    public override async Task<MapProto.DiagramList> GetDiagramsByIds(
      MapProto.GetByIdsRequest request, ServerCallContext context)
    {
      var ct = context.CancellationToken;
      var ids = await VisibleAsync(request.Ids, request.Caller, ct);
      var diagrams = await _diagramService.GetListByIdsAsync(ids.ToList());

      return new MapProto.DiagramList { Diagrams = { diagrams.Values.Select(ToProto) } };
    }

    public override async Task<MapProto.DiagramTypeList> GetDiagramTypes(
      MapProto.GetDiagramTypesRequest request, ServerCallContext context)
    {
      Dictionary<string, DiagramTypeDTO> types;

      if (request.Ids.Count > 0)
      {
        types = await _diagramTypeService.GetListByTypeIdsAsync(request.Ids.ToList());
      }
      else if (request.Names.Count > 0)
      {
        types = await _diagramTypeService.GetListByTypeNamesAsync(request.Names.ToList());
      }
      else
      {
        types = await _diagramTypeService.GetDiagramTypesByFilter(new GetDiagramTypesByFilterDTO(
          string.IsNullOrEmpty(request.Filter) ? null : request.Filter,
          string.IsNullOrEmpty(request.StartId) ? null : request.StartId,
          request.Forward,
          request.Count > 0 ? request.Count : 1000));
      }

      return new MapProto.DiagramTypeList { DiagramTypes = { types.Values.Select(ToProto) } };
    }

    // ── Levels ────────────────────────────────────────────────────────────────────────────────

    public override async Task<MapProto.LevelList> GetLevels(
      Empty request, ServerCallContext context)
    {
      var levels = await _levelService.GetAllZooms();
      return new MapProto.LevelList { Levels = { levels.Values.Select(ToProto) } };
    }

    public override async Task<MapProto.GetLevelResponse> GetLevel(
      MapProto.GetLevelRequest request, ServerCallContext context)
    {
      var level = await _levelService.GetByZoomLevel(request.ZoomLevel ?? string.Empty);
      return level is null
        ? new MapProto.GetLevelResponse { Found = false }
        : new MapProto.GetLevelResponse { Found = true, Level = ToProto(level) };
    }

    public override async Task<MapProto.LevelList> UpdateLevels(
      MapProto.LevelList request, ServerCallContext context)
    {
      var dtos = request.Levels
        .Select(level => new LevelDTO
        {
          id = level.Id,
          zoom_level = level.ZoomLevel,
          zoom_min = level.ZoomMin,
          zoom_max = level.ZoomMax,
        })
        .ToList();

      var updated = await _levelService.UpdateAsync(dtos);
      return new MapProto.LevelList { Levels = { updated.Select(ToProto) } };
    }

    public override async Task<MapProto.OperationStatus> DeleteLevels(
      MapProto.ObjectIds request, ServerCallContext context)
    {
      await _levelService.RemoveAsync(request.Ids.ToList());
      return Ok();
    }

    // ── Shared helpers ───────────────────────────────────────────────────────────────────────

    private async Task<HashSet<string>> VisibleAsync(
      IEnumerable<string> ids, MapProto.Caller? caller, CancellationToken ct)
    {
      var requested = ids
        .Where(id => !string.IsNullOrWhiteSpace(id))
        .Distinct(StringComparer.Ordinal)
        .ToList();

      return await _rightsService.VisibleAsync(requested, ToCaller(caller), ct);
    }

    // The reverse index from a node to the camera it is: the canonical row in camera_node, or the
    // placement row of a picture. Reads name a camera at all only because of these.
    internal Task<Dictionary<string, string>> CameraIdsAsync(
      IEnumerable<string> ids, CancellationToken ct) =>
      CameraIdLookup.ResolveAsync(_db, ids, ct);

    private async Task<MapProto.Figures> FiguresAsync(
      IReadOnlyCollection<string> ids, CancellationToken ct)
    {
      var geo = await _geoService.GetGeoObjectsAsync(ids.ToList());
      var figures = await _mapService.GetFigures(geo);
      return await ToProtoFiguresAsync(figures, ids, ct);
    }

    private async Task<MapProto.Figures> ToProtoFiguresAsync(
      FiguresDTO figures, IEnumerable<string> ids, CancellationToken ct)
    {
      var requested = ids
        .Where(id => !string.IsNullOrWhiteSpace(id))
        .Distinct(StringComparer.Ordinal)
        .ToList();

      var markers = await _mapService.GetAsync(requested);
      var top = await _mapService.GetTopChildren(requested);
      var cameras = await CameraIdsAsync(requested, ct);
      var kinds = await NodeKindLookup.ResolveAsync(_db, requested, ct);
      var visuals = await _visualService.GetListByIdsAsync(requested);

      var result = new MapProto.Figures { AddTracks = figures.add_tracks };
      foreach (var figure in figures.figs)
      {
        var id = figure.id ?? string.Empty;
        markers.TryGetValue(id, out var marker);
        result.Figs.Add(new MapProto.Figure
        {
          Id = id,
          ParentId = figure.parent_id ?? string.Empty,
          Name = figure.name ?? string.Empty,
          Geometry = GeometryProtoConvert.ToProto(figure.geometry),
          Radius = figure.radius ?? 0,
          ZoomLevel = figure.zoom_level ?? string.Empty,
          ExtraProps = { (figure.extra_props ?? []).Select(ToProto) },
          OwnerId = figure.owner_id ?? marker?.owner_id ?? string.Empty,
          HasChildren = top.ContainsKey(id),
          CameraId = cameras.GetValueOrDefault(id) ?? string.Empty,
          Version = marker?.Version ?? figure.Version,
          NodeKind = NodeKindLookup.Name(kinds.GetValueOrDefault(id, NodeKind.Object)),
          Visual = visuals.TryGetValue(id, out var visual) ? ToProto(visual) : null,
        });
      }

      return result;
    }

    // A replica answers with its owner's state and alarms; the rule itself is OwnerView's, which
    // the watch stream uses too.
    private Task<Dictionary<string, ObjectStateDTO>> StatesForAsync(
      IReadOnlyCollection<string> ids, CancellationToken ct) =>
      OwnerView.StatesAsync(_mapService, _stateService, ids);

    private async Task<List<MapProto.AlarmState>> AlarmsForAsync(
      IReadOnlyCollection<string> ids, CancellationToken ct)
    {
      var alarms = await OwnerView.AlarmsAsync(_mapService, _stateService, ids);
      return ids
        .Where(alarms.ContainsKey)
        .Select(id => ToProto(alarms[id]))
        .ToList();
    }

    private async Task<MapProto.TrackPointList> ToProtoTracksAsync(
      List<TrackPointDTO> tracks, MapProto.Caller? caller, CancellationToken ct)
    {
      var ids = tracks
        .Select(t => t.figure?.id)
        .Where(id => !string.IsNullOrEmpty(id))
        .Distinct(StringComparer.Ordinal)
        .ToList()!;
      var visible = await _rightsService.VisibleAsync(ids, ToCaller(caller), ct);

      return new MapProto.TrackPointList
      {
        Tracks =
        {
          tracks
            .Where(t => t.figure?.id is not null && visible.Contains(t.figure.id))
            .Select(ToProto)
        }
      };
    }

    private static MapProto.Marker ToMarker(
      BaseMarkerDTO marker, bool hasChildren, string? cameraId, NodeKind kind) => new()
    {
      Id = marker.id ?? string.Empty,
      ParentId = marker.parent_id ?? string.Empty,
      OwnerId = marker.owner_id ?? string.Empty,
      Name = marker.name ?? string.Empty,
      HasChildren = hasChildren,
      CameraId = cameraId ?? string.Empty,
      Version = marker.Version,
      NodeKind = NodeKindLookup.Name(kind),
    };

    private static MapProto.ExtraProperty ToProto(ObjExtraPropertyDTO prop) => new()
    {
      PropName = prop.prop_name ?? string.Empty,
      StrVal = prop.str_val ?? string.Empty,
      VisualType = prop.visual_type ?? string.Empty,
    };

    private static MapProto.Visual ToProto(VisualDTO visual)
    {
      var proto = new MapProto.Visual
      {
        ObjectId = visual.object_id ?? string.Empty,
        Color = visual.color ?? string.Empty,
        Image = visual.image is null ? ByteString.Empty : ByteString.CopyFrom(visual.image),
        ImageWidth = visual.image_width ?? 0,
        ImageHeight = visual.image_height ?? 0,
        Text = visual.text ?? string.Empty,
      };

      // An absent rotation means "upright"; writing it marks the optional field as present.
      if (visual.rotation_deg is { } rotation)
        proto.RotationDeg = rotation;

      return proto;
    }

    private static MapProto.ObjectState ToProto(ObjectStateDTO state) => new()
    {
      Id = state.id ?? string.Empty,
      States = { state.states ?? [] },
      Timestamp = state.timestamp is null ? null : Timestamp.FromDateTime(
        DateTime.SpecifyKind(state.timestamp.Value, DateTimeKind.Utc)),
      Version = state.Version,
    };

    private static MapProto.AlarmState ToProto(AlarmState state) => new()
    {
      Id = state.id ?? string.Empty,
      Alarm = state.alarm ?? false,
      ChildrenAlarms = state.children_alarms,
      Version = state.Version,
    };

    private static MapProto.StateDescription ToProto(ObjectStateDescriptionDTO description) => new()
    {
      Id = description.id ?? string.Empty,
      State = description.state ?? string.Empty,
      StateDescr = description.state_descr ?? string.Empty,
      StateColor = description.state_color ?? string.Empty,
      Alarm = description.alarm ?? false,
    };

    private static MapProto.Event ToProto(EventDTO evt) => new()
    {
      Timestamp = Timestamp.FromDateTime(
        DateTime.SpecifyKind(evt.timestamp == default ? DateTime.UtcNow : evt.timestamp, DateTimeKind.Utc)),
      Id = evt.id ?? string.Empty,
      ObjectId = evt.object_id ?? string.Empty,
      EventName = evt.event_name ?? string.Empty,
      EventPriority = evt.event_priority,
      Param0 = evt.param0 ?? string.Empty,
      Param1 = evt.param1 ?? string.Empty,
      ExtraProps = { (evt.extra_props ?? []).Select(ToProto) },
    };

    private static MapProto.TrackPoint ToProto(TrackPointDTO track) => new()
    {
      Id = track.id ?? string.Empty,
      Figure = track.figure is null
        ? null
        : new MapProto.GeoObject
        {
          Id = track.figure.id ?? string.Empty,
          Location = GeometryProtoConvert.ToProto(track.figure.location),
          Radius = track.figure.radius ?? 0,
          ZoomLevel = track.figure.zoom_level ?? string.Empty,
        },
      Timestamp = track.timestamp is null ? null : Timestamp.FromDateTime(
        DateTime.SpecifyKind(track.timestamp.Value, DateTimeKind.Utc)),
      ExtraProps = { (track.extra_props ?? []).Select(ToProto) },
    };

    private static MapProto.Value ToProto(ValueDTO value)
    {
      var proto = new MapProto.Value
      {
        Id = value.id ?? string.Empty,
        OwnerId = value.owner_id ?? string.Empty,
        Name = value.name ?? string.Empty,
        Version = value.Version,
      };

      proto.Value_ = value.value switch
      {
        double d => new MapProto.ValueType { DoubleValue = d },
        int i => new MapProto.ValueType { IntValue = i },
        string s => new MapProto.ValueType { StringValue = s },
        _ => null,
      };

      return proto;
    }

    private static MapProto.Level ToProto(LevelDTO level) => new()
    {
      Id = level.id ?? string.Empty,
      ZoomLevel = level.zoom_level ?? string.Empty,
      ZoomMin = level.zoom_min,
      ZoomMax = level.zoom_max,
    };

    private static BoxDTO ToBoxDto(MapProto.Box? box)
    {
      var result = new BoxDTO();
      if (box?.Wn is not null && box.Es is not null)
      {
        // BoxDTO is in the geometry's (X, Y) order, which is (lon, lat): NTS stores a point as
        // Coordinate(lon, lat), and GeoService builds the rectangle straight from these two.
        result.wn = [box.Wn.Lon, box.Wn.Lat];
        result.es = [box.Es.Lon, box.Es.Lat];
      }

      result.zoom = box is { HasZoom: true } ? box.Zoom : null;
      result.ids = box?.Ids.ToList();
      result.count = box is { HasCount: true } ? box.Count : null;
      result.not_in_zone = box?.NotInZone ?? false;
      result.canvas_id = string.IsNullOrEmpty(box?.CanvasId) ? null : box.CanvasId;
      result.zone = (box?.Zone ?? [])
        .Select(GeometryProtoConvert.FromProto)
        .Where(geometry => geometry is not null)
        .ToList()!;

      return result;
    }

    private static void CopyBox(BoxDTO source, BoxDTO target)
    {
      target.wn = source.wn;
      target.es = source.es;
      target.zoom = source.zoom;
      target.ids = source.ids;
      target.count = source.count;
      target.zone = source.zone;
      target.not_in_zone = source.not_in_zone;
      target.canvas_id = source.canvas_id;
    }
  }
}
