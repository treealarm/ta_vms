using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using VmsMap.Adapter;
using VmsMap.Db;
using VmsMap.Demo;
using VmsMap.Domain;
using VmsMap.Domain.Arming;
using VmsMap.PubSub;
using VmsMap.Watch;
using MapProto = Vms.Map;

namespace VmsMap.Grpc
{
  // The write half of MapService: convert, call the repositories, wrap the steps that must not
  // half-happen in one transaction. The spatial half (spaces, plans, placements, landmarks and
  // calibrations) is in MapServiceImpl.Spatial.cs.
  public partial class MapServiceImpl : MapProto.MapService.MapServiceBase
  {
    private const int FileChunkBytes = 1024 * 1024;

    private readonly VmsMapDbContext _db;
    private readonly IMapService _mapService;
    private readonly IMapUpdateService _mapUpdateService;
    private readonly IGeoService _geoService;
    private readonly ILevelService _levelService;
    private readonly ITrackService _trackService;
    private readonly IStatesUpdateService _statesUpdateService;
    private readonly IStateService _stateService;
    private readonly IEventsService _eventsService;
    private readonly EventsWriter _eventsWriter;
    private readonly IValuesServiceInternal _valuesService;
    private readonly ITracksUpdateService _tracksUpdateService;
    private readonly IDiagramServiceInternal _diagramService;
    private readonly IDiagramTypeService _diagramTypeService;
    private readonly IRightServiceInternal _rightService;
    private readonly IRightsService _rightsService;
    private readonly IRightsVersionNotifier _rightsVersionNotifier;
    private readonly VisualService _visualService;
    private readonly FileService _fileService;
    private readonly SpatialDb _spatialDb;
    private readonly MapWatchPoller _watchPoller;
    private readonly DemoSeeder _demoSeeder;
    private readonly VmsMap.Domain.Arming.EffectiveArmingResolver _armingResolver;
    private readonly VmsMap.Domain.Arming.NodeArmingService _nodeArming;
    private readonly VmsMap.Domain.Arming.IDeviceSettingsService _deviceSettings;
    private readonly VmsMap.Domain.Control.ControlObjectQuery _controlObjects;
    private readonly AlarmStore _alarmStore;
    private readonly IDeviceDiagramService _deviceDiagrams;

    public MapServiceImpl(
      VmsMapDbContext db,
      IMapService mapService,
      IMapUpdateService mapUpdateService,
      IGeoService geoService,
      ILevelService levelService,
      ITrackService trackService,
      IStatesUpdateService statesUpdateService,
      IStateService stateService,
      IEventsService eventsService,
      EventsWriter eventsWriter,
      IValuesServiceInternal valuesService,
      ITracksUpdateService tracksUpdateService,
      IDiagramServiceInternal diagramService,
      IDiagramTypeService diagramTypeService,
      IRightServiceInternal rightService,
      IRightsService rightsService,
      IRightsVersionNotifier rightsVersionNotifier,
      VisualService visualService,
      FileService fileService,
      SpatialDb spatialDb,
      MapWatchPoller watchPoller,
      DemoSeeder demoSeeder,
      VmsMap.Domain.Arming.EffectiveArmingResolver armingResolver,
      VmsMap.Domain.Arming.NodeArmingService nodeArming,
      VmsMap.Domain.Arming.IDeviceSettingsService deviceSettings,
      VmsMap.Domain.Control.ControlObjectQuery controlObjects,
      AlarmStore alarmStore,
      IDeviceDiagramService deviceDiagrams)
    {
      _db = db;
      _mapService = mapService;
      _mapUpdateService = mapUpdateService;
      _geoService = geoService;
      _levelService = levelService;
      _trackService = trackService;
      _statesUpdateService = statesUpdateService;
      _stateService = stateService;
      _eventsService = eventsService;
      _eventsWriter = eventsWriter;
      _valuesService = valuesService;
      _tracksUpdateService = tracksUpdateService;
      _diagramService = diagramService;
      _diagramTypeService = diagramTypeService;
      _rightService = rightService;
      _rightsService = rightsService;
      _rightsVersionNotifier = rightsVersionNotifier;
      _visualService = visualService;
      _fileService = fileService;
      _spatialDb = spatialDb;
      _watchPoller = watchPoller;
      _demoSeeder = demoSeeder;
      _armingResolver = armingResolver;
      _nodeArming = nodeArming;
      _deviceSettings = deviceSettings;
      _controlObjects = controlObjects;
      _deviceDiagrams = deviceDiagrams;
      _alarmStore = alarmStore;
    }

    // ── Objects ───────────────────────────────────────────────────────────────────────────────

    public override async Task<MapProto.ObjectList> UpdateObjects(
      MapProto.ObjectList request, ServerCallContext context)
    {
      var dtos = request.Objects.Select(ToDto).ToList();

      await using var tx = await BeginTransactionAsync(context.CancellationToken);

      var updated = await _mapUpdateService.UpdateObjects(dtos);

      if (tx is not null)
        await tx.CommitAsync(context.CancellationToken);

      // A node moved under another parent takes its subtree with it, and a camera standing in it
      // may now be inside a zone — or outside one.
      await _spatialDb.ReconcileArmingAsync(
        updated.Select(o => o.id).ToList(), context.CancellationToken);

      return new MapProto.ObjectList { Objects = { updated.Select(ToProto) } };
    }

    public override async Task<MapProto.OperationStatus> UpdateProperties(
      MapProto.ObjectPropertiesList request, ServerCallContext context)
    {
      // Arming is the map's own state, not a property of an object: a property written here would
      // be stored, shown to a reader and read by nobody, and it would skip the journal entry and
      // the alarm re-check an arming change owes. `object_arming` is the one place that says it.
      if (request.Objects.Any(item => item.Properties.Any(p => p.PropName == ArmMode.Armed)))
        throw new RpcException(new Status(
          StatusCode.InvalidArgument, "arm a node with SetNodeArming; 'armed' is not a property"));

      await using var tx = await BeginTransactionAsync(context.CancellationToken);

      foreach (var item in request.Objects)
      {
        var dto = new ObjPropsDTO
        {
          id = item.Id,
          extra_props = item.Properties.Select(ToDto).ToList()
        };

        if (request.ReplaceProps)
        {
          await _mapService.UpdatePropAsync(new[] { dto });
        }
        else
        {
          await _mapUpdateService.UpdateProperties(dto);
        }
      }

      if (tx is not null)
        await tx.CommitAsync(context.CancellationToken);
      return Ok();
    }

    public override async Task<MapProto.OperationStatus> UpdateVisual(
      MapProto.VisualList request, ServerCallContext context)
    {
      var dtos = request.Visuals.Select(v => new VisualDTO
      {
        object_id = v.ObjectId,
        color = v.Color,
        image = v.Image.IsEmpty ? null : v.Image.ToByteArray(),
        image_width = v.ImageWidth == 0 ? null : v.ImageWidth,
        image_height = v.ImageHeight == 0 ? null : v.ImageHeight,
        rotation_deg = v.HasRotationDeg ? v.RotationDeg : null,
        text = v.Text
      }).ToList();

      await _visualService.UpdateListAsync(dtos);
      return Ok();
    }

    public override async Task<MapProto.Figures> UpdateFigures(
      MapProto.Figures request, ServerCallContext context)
    {
      var figures = new FiguresDTO
      {
        add_tracks = request.AddTracks,
        figs = request.Figs.Select(ToDto).ToList()
      };

      await using var tx = await BeginTransactionAsync(context.CancellationToken);

      await _mapUpdateService.UpdateFigures(figures);

      if (tx is not null)
        await tx.CommitAsync(context.CancellationToken);

      return new MapProto.Figures
      {
        AddTracks = request.AddTracks,
        Figs = { figures.figs.Select(ToProto) }
      };
    }

    public override async Task<MapProto.OperationStatus> DeleteObjects(
      MapProto.ObjectIds request, ServerCallContext context)
    {
      // The same removal a space, a landmark or a camera's picture takes: side rows, device
      // diagrams, the alarms above and the cameras that stood in the subtree all leave with the
      // nodes. The removal owns its own transaction, so the notification and the arming reconcile
      // happen after the commit rather than inside it.
      var removed = await _spatialDb.RemoveSubtreeAsync(
        request.Ids.ToList(), context.CancellationToken);

      return new MapProto.OperationStatus
      {
        Success = true,
        Message = $"{removed} object(s) deleted"
      };
    }

    // ── States, events, values, tracks ───────────────────────────────────────────────────────

    public override async Task<MapProto.OperationStatus> UpdateStates(
      MapProto.ObjectStateList request, ServerCallContext context)
    {
      var dtos = request.States.Select(s => new ObjectStateDTO
      {
        id = s.Id,
        states = s.States.ToList(),
        timestamp = s.Timestamp?.ToDateTime()
      }).ToList();

      await _statesUpdateService.UpdateStates(dtos);
      return Ok();
    }

    public override async Task<MapProto.OperationStatus> UpdateStateDescriptions(
      MapProto.StateDescriptionList request, ServerCallContext context)
    {
      var dtos = request.Descriptions.Select(d => new ObjectStateDescriptionDTO
      {
        id = d.Id,
        state = d.State,
        state_descr = d.StateDescr,
        state_color = d.StateColor,
        alarm = d.Alarm
      }).ToList();

      await _statesUpdateService.UpdateStateDescrs(dtos);
      return Ok();
    }

    public override async Task<MapProto.OperationStatus> UpdateEvents(
      MapProto.EventList request, ServerCallContext context)
    {
      var dtos = request.Events.Select(e => new EventDTO
      {
        id = e.Id,
        object_id = e.ObjectId,
        event_name = e.EventName,
        event_priority = e.EventPriority,
        param0 = e.Param0,
        param1 = e.Param1,
        timestamp = e.Timestamp?.ToDateTime() ?? DateTime.UtcNow,
        extra_props = e.ExtraProps.Select(ToDto).ToList()
      }).ToList();

      await _eventsWriter.WriteAsync(dtos, context.CancellationToken);
      return Ok();
    }

    public override async Task<MapProto.OperationStatus> UpdateValues(
      MapProto.ValueList request, ServerCallContext context)
    {
      var dtos = request.Values.Select(v => new ValueDTO
      {
        id = v.Id,
        owner_id = v.OwnerId,
        name = v.Name,
        value = FromProto(v.Value_)
      }).ToList();

      await _valuesService.UpdateListAsync(dtos);
      return Ok();
    }

    public override async Task<MapProto.OperationStatus> DeleteValues(
      MapProto.ObjectIds request, ServerCallContext context)
    {
      await _valuesService.RemoveAsync(request.Ids.ToList());
      return Ok();
    }

    public override async Task<MapProto.OperationStatus> UpdateTracks(
      MapProto.TrackPointList request, ServerCallContext context)
    {
      var dtos = request.Tracks.Select(ToDto).ToList();
      await _tracksUpdateService.AddTracks(dtos);
      return Ok();
    }

    // ── Diagrams ─────────────────────────────────────────────────────────────────────────────

    public override async Task<MapProto.DiagramList> UpdateDiagrams(
      MapProto.DiagramList request, ServerCallContext context)
    {
      var dtos = request.Diagrams.Select(ToDto).ToList();

      // A representation carries no type or picture of its own: those belong to the diagram it
      // shows, and writing them through a copy would fork the content in one place only. Its own
      // place stays writable — that is what a copy is.
      var rewritten = dtos
        .Where(d => !string.IsNullOrWhiteSpace(d.dgr_type) || !string.IsNullOrWhiteSpace(d.file_id))
        .Select(d => d.id)
        .Where(id => !string.IsNullOrWhiteSpace(id))
        .Select(id => id!)
        .Distinct(StringComparer.Ordinal)
        .ToList();

      if (rewritten.Count > 0)
      {
        var resolved = await DiagramRepresentations.ResolveAsync(
          _db, rewritten, context.CancellationToken);
        // A node with no row of its own and nothing to represent is a diagram being drawn for the
        // first time: there is no copy to protect, and the row is the one this request writes.
        if (rewritten.Any(id => resolved[id].Row?.is_representation == true))
          throw new RpcException(new Status(StatusCode.InvalidArgument,
            "A representation takes its type and picture from the diagram it shows; " +
            "edit that diagram instead"));
      }

      try
      {
        await _diagramService.UpdateListAsync(dtos);
      }
      catch (DiagramConfigException ex)
      {
        throw new RpcException(new Status(StatusCode.InvalidArgument, ex.Message));
      }

      return new MapProto.DiagramList { Diagrams = { dtos.Select(ToProto) } };
    }

    public override async Task<MapProto.OperationStatus> DeleteDiagrams(
      MapProto.ObjectIds request, ServerCallContext context)
    {
      await _diagramService.RemoveAsync(request.Ids.ToList());
      return Ok();
    }

    public override async Task<MapProto.DiagramTypeList> UpdateDiagramTypes(
      MapProto.DiagramTypeList request, ServerCallContext context)
    {
      var dtos = request.DiagramTypes.Select(ToDto).ToList();

      try
      {
        await _diagramTypeService.UpdateListAsync(dtos);
      }
      catch (DiagramTypeConfigException ex)
      {
        throw new RpcException(new Status(StatusCode.InvalidArgument, ex.Message));
      }

      return new MapProto.DiagramTypeList { DiagramTypes = { dtos.Select(ToProto) } };
    }

    public override async Task<MapProto.OperationStatus> DeleteDiagramTypes(
      MapProto.ObjectIds request, ServerCallContext context)
    {
      await _diagramTypeService.DeleteAsync(request.Ids.ToList());
      return Ok();
    }

    /// <summary>
    /// Draws a device on the map — the administrator's path: the diagram is an object of the named
    /// space, drawn from the template that matches the device's model (or a blank sheet when none
    /// does), with the device's configured parts bound to its regions; on a diagram's canvas the
    /// request shows the diagram there as a representation, in one of the canvas type's regions or
    /// at a rectangle of its own. No canvas named keeps it with the camera: the node hangs under the
    /// camera's picture and nothing is drawn beside it.
    /// </summary>
    public override async Task<MapProto.Marker> AttachDeviceDiagram(
      MapProto.AttachDeviceDiagramRequest request, ServerCallContext context)
    {
      var ct = context.CancellationToken;
      if (string.IsNullOrWhiteSpace(request.SourceId))
        throw new RpcException(new Status(StatusCode.InvalidArgument, "A device is required"));

      var sourceKind = string.IsNullOrWhiteSpace(request.SourceKind)
        ? DeviceObjectId.CameraSource
        : request.SourceKind;
      var slot = string.IsNullOrWhiteSpace(request.Slot)
        ? IDeviceDiagramService.DefaultSlot
        : request.Slot;

      // The request matrix: a space takes a point, a diagram's canvas takes a region or a
      // rectangle, and mixing the two kinds of place is a mistake rather than something to guess
      // at. The service checks the same matrix against what the canvas really is.
      var spaceId = string.IsNullOrWhiteSpace(request.SpaceId) ? null : request.SpaceId;
      var canvasId = string.IsNullOrWhiteSpace(request.CanvasId) ? null : request.CanvasId;
      var regionId = string.IsNullOrWhiteSpace(request.RegionId) ? null : request.RegionId;
      var geometry = ToDto(request.Geometry);

      if (spaceId is not null && canvasId is not null)
        throw new RpcException(new Status(StatusCode.InvalidArgument,
          "Name a space or a diagram's canvas, not both"));
      if (regionId is not null && geometry is not null)
        throw new RpcException(new Status(StatusCode.InvalidArgument,
          "A diagram stands in a region or at a rectangle, not both"));
      if ((regionId is not null || geometry is not null) && canvasId is null)
        throw new RpcException(new Status(StatusCode.InvalidArgument,
          "A region or a rectangle places a diagram on a diagram's canvas"));
      if (request.At is not null && canvasId is not null)
        throw new RpcException(new Status(StatusCode.InvalidArgument,
          "A diagram's canvas takes a region or a rectangle, not a point"));

      DiagramTarget? target =
        canvasId is not null ? new DiagramTarget(canvasId, regionId, geometry)
        : spaceId is not null ? new DiagramTarget(spaceId, Lat: request.At?.Lat, Lon: request.At?.Lon)
        : null;

      string? objectId;
      try
      {
        objectId = await _deviceDiagrams.PlaceDeviceDiagramAsync(
          sourceKind, request.SourceId, request.DiagramTypeId, slot, target, ct);
      }
      catch (DeviceDiagramException ex)
      {
        // The place the request names is not one the map can give: the message says which, and the
        // administrator is the one who can fix it.
        throw new RpcException(new Status(StatusCode.InvalidArgument, ex.Message));
      }

      if (objectId is null)
        throw new RpcException(new Status(StatusCode.FailedPrecondition,
          "The diagram could not be created: the space does not exist, the camera is not placed, " +
          "or the template is gone"));

      var marker = (await _mapService.GetAsync([objectId])).GetValueOrDefault(objectId);
      if (marker is null)
        throw new RpcException(new Status(StatusCode.Internal, "The diagram node is missing"));

      return ToMarker(marker, false, null, NodeKind.Diagram);
    }

    /// <summary>
    /// Binds a diagram the administrator drew to a device: it is maintained from then on — the
    /// template that describes the device and a replica per configured part — and keeps its place
    /// and what was drawn in it by hand. Repeating the same bind changes nothing.
    /// </summary>
    public override async Task<MapProto.Marker> BindDeviceDiagram(
      MapProto.BindDeviceDiagramRequest request, ServerCallContext context)
    {
      var ct = context.CancellationToken;
      if (string.IsNullOrWhiteSpace(request.NodeId))
        throw new RpcException(new Status(StatusCode.InvalidArgument, "A diagram is required"));
      if (string.IsNullOrWhiteSpace(request.SourceId))
        throw new RpcException(new Status(StatusCode.InvalidArgument, "A device is required"));

      var sourceKind = string.IsNullOrWhiteSpace(request.SourceKind)
        ? DeviceObjectId.CameraSource
        : request.SourceKind;
      var slot = string.IsNullOrWhiteSpace(request.Slot)
        ? IDeviceDiagramService.DefaultSlot
        : request.Slot;

      string? nodeId;
      try
      {
        nodeId = await _deviceDiagrams.BindAsync(
          request.NodeId, sourceKind, request.SourceId, request.DiagramTypeId, slot, ct);
      }
      catch (DeviceDiagramException ex)
      {
        // The node, the slot or the template is not one the map can bind: the message says which,
        // and the administrator is the one who can fix it.
        throw new RpcException(new Status(StatusCode.InvalidArgument, ex.Message));
      }

      var marker = nodeId is null
        ? null
        : (await _mapService.GetAsync([nodeId])).GetValueOrDefault(nodeId);
      if (marker is null)
        throw new RpcException(new Status(StatusCode.Internal, "The diagram node is missing"));

      return ToMarker(marker, false, null, NodeKind.Diagram);
    }

    /// <summary>
    /// Takes a diagram away from its device: the replicas the map made go, the diagram itself stays
    /// and is an ordinary mnemoscheme again. Unbinding what is not bound is a successful no-op.
    /// </summary>
    public override async Task<MapProto.OperationStatus> UnbindDeviceDiagram(
      MapProto.UnbindDeviceDiagramRequest request, ServerCallContext context)
    {
      if (string.IsNullOrWhiteSpace(request.NodeId))
        throw new RpcException(new Status(StatusCode.InvalidArgument, "A diagram is required"));

      await _deviceDiagrams.UnbindAsync(request.NodeId, context.CancellationToken);
      return Ok();
    }

    public override async Task<MapProto.DeviceDiagramBinding> GetDeviceDiagramBinding(
      MapProto.GetDeviceDiagramBindingRequest request, ServerCallContext context)
    {
      var binding = await _deviceDiagrams.FindBindingAsync(
        request.NodeId, context.CancellationToken);
      if (binding is null)
        return new MapProto.DeviceDiagramBinding();

      return new MapProto.DeviceDiagramBinding
      {
        Bound = true,
        SourceKind = binding.SourceKind,
        SourceId = binding.SourceId,
        Slot = binding.Slot,
      };
    }

    // ── Demo world ────────────────────────────────────────────────────────────────────────────

    public override async Task<MapProto.DemoReport> InstallDemo(
      MapProto.InstallDemoRequest request, ServerCallContext context)
    {
      var world = RequireWorld(request.DemoKey);
      var report = await _demoSeeder.InstallAsync(world, context.CancellationToken);
      return ToProto(report);
    }

    public override async Task<MapProto.DemoReport> RemoveDemo(
      MapProto.InstallDemoRequest request, ServerCallContext context)
    {
      var world = RequireWorld(request.DemoKey);
      var report = await _demoSeeder.RemoveAsync(world.Key, context.CancellationToken);
      return ToProto(report);
    }

    public override async Task<MapProto.DemoStatus> GetDemoStatus(
      MapProto.InstallDemoRequest request, ServerCallContext context)
    {
      var world = RequireWorld(request.DemoKey);
      var status = await _demoSeeder.GetStatusAsync(world.Key, context.CancellationToken);

      return new MapProto.DemoStatus
      {
        DemoKey = status.Key,
        Installed = status.Installed,
        Version = status.Version,
        Entities = status.Entities,
      };
    }

    private static DemoWorld RequireWorld(string key) =>
      DemoWorlds.For(key) ?? throw new RpcException(new Status(
        StatusCode.InvalidArgument, $"There is no demo world '{key}'"));

    private static MapProto.DemoReport ToProto(DemoReport report)
    {
      var proto = new MapProto.DemoReport
      {
        DemoKey = report.Key,
        Version = report.Version,
        Created = report.Created,
        Updated = report.Updated,
        Removed = report.Removed,
      };

      proto.SkippedCameras.AddRange(report.SkippedCameras);
      return proto;
    }

    // ── Rights ───────────────────────────────────────────────────────────────────────────────

    public override async Task<MapProto.OperationStatus> UpdateRights(
      MapProto.RightList request, ServerCallContext context)
    {
      var dtos = request.Rights.Select(r => new RightDTO
      {
        object_id = r.ObjectId,
        subject = r.Subject,
        action = r.Action
      }).ToList();

      await _rightService.Update(dtos);
      await _rightsVersionNotifier.BumpAsync(context.CancellationToken);
      return Ok();
    }

    public override async Task<MapProto.OperationStatus> DeleteRights(
      MapProto.RightKeyList request, ServerCallContext context)
    {
      var keys = request.Keys.Select(k => new RightDTO
      {
        object_id = k.ObjectId,
        subject = k.Subject,
        action = k.Action
      }).ToList();

      await _rightService.Delete(keys);
      await _rightsVersionNotifier.BumpAsync(context.CancellationToken);
      return Ok();
    }

    // ── Files ────────────────────────────────────────────────────────────────────────────────

    public override async Task<MapProto.FileInfo> PutFile(
      IAsyncStreamReader<MapProto.FileUploadChunk> requestStream,
      ServerCallContext context)
    {
      using var buffer = new MemoryStream();
      string? mime = null;

      await foreach (var chunk in requestStream.ReadAllAsync(context.CancellationToken))
      {
        mime ??= string.IsNullOrEmpty(chunk.Mime) ? null : chunk.Mime;

        if (buffer.Length + chunk.Data.Length > FileService.MaxFileBytes)
        {
          throw new RpcException(new Status(
            StatusCode.InvalidArgument,
            $"A file may not exceed {FileService.MaxFileBytes} bytes"));
        }

        buffer.Write(chunk.Data.Span);
      }

      var file = await _fileService.PutAsync(mime, buffer.ToArray());

      return new MapProto.FileInfo
      {
        Id = Utils.ConvertGuidToObjectId(file.id),
        Mime = file.mime,
        Size = (ulong)file.bytes.Length,
        Etag = file.etag ?? string.Empty
      };
    }

    public override async Task GetFile(
      MapProto.FileRequest request,
      IServerStreamWriter<MapProto.FileChunk> responseStream,
      ServerCallContext context)
    {
      var file = await _fileService.GetAsync(request.Id)
        ?? throw new RpcException(new Status(
          StatusCode.NotFound, $"File '{request.Id}' does not exist"));

      for (var offset = 0; offset < file.bytes.Length; offset += FileChunkBytes)
      {
        var count = Math.Min(FileChunkBytes, file.bytes.Length - offset);
        await responseStream.WriteAsync(new MapProto.FileChunk
        {
          Data = ByteString.CopyFrom(file.bytes, offset, count)
        });
      }
    }

    public override async Task<MapProto.FileInfo> GetFileInfo(
      MapProto.FileRequest request, ServerCallContext context)
    {
      var file = await _fileService.GetAsync(request.Id)
        ?? throw new RpcException(new Status(
          StatusCode.NotFound, $"File '{request.Id}' does not exist"));

      return new MapProto.FileInfo
      {
        Id = Utils.ConvertGuidToObjectId(file.id),
        Mime = file.mime,
        Size = (ulong)file.bytes.Length,
        Etag = file.etag ?? string.Empty
      };
    }

    // ── Conversions ─────────────────────────────────────────────────────────────────────────

    // InMemory, which the tests use, has no transactions; production always runs on PostgreSQL.
    // Null means there is nothing to commit.
    private Task<IDbContextTransaction?> BeginTransactionAsync(CancellationToken ct) =>
      _db.Database.IsRelational()
        ? _db.Database.BeginTransactionAsync(ct)!
        : Task.FromResult<IDbContextTransaction?>(null);

    private static MapProto.OperationStatus Ok() => new() { Success = true };

    private static BaseMarkerDTO ToDto(MapProto.Object obj) => new()
    {
      id = obj.Id,
      parent_id = obj.ParentId,
      owner_id = obj.OwnerId,
      name = obj.Name
    };

    private static MapProto.Object ToProto(BaseMarkerDTO dto) => new()
    {
      Id = dto.id ?? string.Empty,
      ParentId = dto.parent_id ?? string.Empty,
      OwnerId = dto.owner_id ?? string.Empty,
      Name = dto.name ?? string.Empty
    };

    private static ObjExtraPropertyDTO ToDto(MapProto.ExtraProperty prop) => new()
    {
      prop_name = prop.PropName,
      str_val = prop.StrVal,
      visual_type = prop.VisualType
    };

    private static FigureGeoDTO ToDto(MapProto.Figure fig) => new()
    {
      id = fig.Id,
      parent_id = fig.ParentId,
      // A figure write also writes the tree row; dropping the owner here would clear a picture's
      // owner on any shape edit.
      owner_id = fig.HasOwnerId ? fig.OwnerId : null,
      name = fig.Name,
      geometry = GeometryProtoConvert.FromProto(fig.Geometry),
      radius = fig.HasRadius ? fig.Radius : null,
      zoom_level = fig.ZoomLevel,
      extra_props = fig.ExtraProps.Select(ToDto).ToList()
    };

    private static MapProto.Figure ToProto(FigureGeoDTO fig) => new()
    {
      Id = fig.id ?? string.Empty,
      ParentId = fig.parent_id ?? string.Empty,
      Name = fig.name ?? string.Empty,
      OwnerId = fig.owner_id ?? string.Empty,
      Geometry = GeometryProtoConvert.ToProto(fig.geometry),
      Radius = fig.radius ?? 0,
      ZoomLevel = fig.zoom_level ?? string.Empty,
      ExtraProps = { (fig.extra_props ?? new List<ObjExtraPropertyDTO>()).Select(p => new MapProto.ExtraProperty
      {
        PropName = p.prop_name ?? string.Empty,
        StrVal = p.str_val ?? string.Empty,
        VisualType = p.visual_type ?? string.Empty
      }) }
    };

    private static TrackPointDTO ToDto(MapProto.TrackPoint track) => new()
    {
      id = track.Id,
      figure = track.Figure == null
        ? null
        : new GeoObjectDTO
        {
          id = track.Figure.Id,
          location = GeometryProtoConvert.FromProto(track.Figure.Location),
          radius = track.Figure.HasRadius ? track.Figure.Radius : null,
          zoom_level = track.Figure.ZoomLevel
        },
      timestamp = track.Timestamp?.ToDateTime(),
      extra_props = track.ExtraProps.Select(ToDto).ToList()
    };

    private static DiagramDTO ToDto(MapProto.Diagram diagram) => new()
    {
      id = diagram.Id,
      geometry = ToDto(diagram.Geometry),
      region_id = diagram.RegionId,
      dgr_type = diagram.DgrType,
      file_id = diagram.FileId,
      placement = ToDto(diagram.Placement),
      is_representation = diagram.IsRepresentation
    };

    private static MapProto.Diagram ToProto(DiagramDTO dto) => new()
    {
      Id = dto.id ?? string.Empty,
      Geometry = ToProto(dto.geometry),
      RegionId = dto.region_id ?? string.Empty,
      DgrType = dto.dgr_type ?? string.Empty,
      FileId = dto.file_id ?? string.Empty,
      Placement = ToProto(dto.placement),
      IsRepresentation = dto.is_representation
    };

    private static DiagramCoordDTO? ToDto(MapProto.DiagramCoordinate? coord) => coord == null
      ? null
      : new DiagramCoordDTO
      {
        top = coord.Top,
        left = coord.Left,
        width = coord.Width,
        height = coord.Height,
        rotation = coord.HasRotation ? coord.Rotation : null
      };

    private static MapProto.DiagramCoordinate? ToProto(DiagramCoordDTO? coord) => coord == null
      ? null
      : new MapProto.DiagramCoordinate
      {
        Top = coord.top,
        Left = coord.left,
        Width = coord.width,
        Height = coord.height,
        Rotation = coord.rotation ?? 0
      };

    private static DiagramTypeDTO ToDto(MapProto.DiagramType type) => new()
    {
      id = type.Id,
      name = type.Name,
      file_id = type.FileId,
      source_kind = string.IsNullOrEmpty(type.SourceKind) ? null : type.SourceKind,
      device_manufacturer = type.DeviceManufacturer,
      device_model = type.DeviceModel,
      auto_attach = type.AutoAttach,
      regions = type.Regions.Select(r => new DiagramTypeRegionDTO
      {
        region_key = r.RegionKey,
        geometry = ToDto(r.Geometry),
        styles = r.Styles.ToDictionary(kv => kv.Key, kv => kv.Value),
        part = r.Part is null
          ? null
          : new DiagramTypePartDTO
          {
            part_kind = r.Part.PartKind,
            match_by = r.Part.MatchBy,
            match_value = r.Part.MatchValue,
          },
      }).ToList()
    };

    private static MapProto.DiagramType ToProto(DiagramTypeDTO dto) => new()
    {
      Id = dto.id ?? string.Empty,
      Name = dto.name ?? string.Empty,
      FileId = dto.file_id ?? string.Empty,
      SourceKind = dto.source_kind ?? string.Empty,
      DeviceManufacturer = dto.device_manufacturer ?? string.Empty,
      DeviceModel = dto.device_model ?? string.Empty,
      AutoAttach = dto.auto_attach,
      Regions =
      {
        (dto.regions ?? new List<DiagramTypeRegionDTO>()).Select(r => new MapProto.DiagramTypeRegion
        {
          RegionKey = r.region_key ?? string.Empty,
          Geometry = ToProto(r.geometry) ?? new MapProto.DiagramCoordinate(),
          Styles = { r.styles ?? new Dictionary<string, string>() },
          Part = r.part is null
            ? null
            : new MapProto.DiagramTypeRegionPart
            {
              PartKind = r.part.part_kind ?? string.Empty,
              MatchBy = r.part.match_by ?? string.Empty,
              MatchValue = r.part.match_value ?? string.Empty,
            },
        })
      }
    };

    private static object? FromProto(MapProto.ValueType? value) => value?.ValueCase switch
    {
      MapProto.ValueType.ValueOneofCase.DoubleValue => value.DoubleValue,
      MapProto.ValueType.ValueOneofCase.IntValue => value.IntValue,
      MapProto.ValueType.ValueOneofCase.StringValue => value.StringValue,
      _ => null
    };
  }
}
