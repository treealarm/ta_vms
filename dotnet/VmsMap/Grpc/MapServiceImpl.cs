using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using VmsMap.Db;
using VmsMap.Domain;
using MapProto = Vms.Map;

namespace VmsMap.Grpc
{
  // The write half of MapService: convert, call the repositories, wrap the steps that must not
  // half-happen in one transaction. Reads arrive with M2/M3.
  public class MapServiceImpl : MapProto.MapService.MapServiceBase
  {
    private const int FileChunkBytes = 1024 * 1024;

    private readonly VmsMapDbContext _db;
    private readonly IMapService _mapService;
    private readonly IMapUpdateService _mapUpdateService;
    private readonly IStatesUpdateService _statesUpdateService;
    private readonly IStateService _stateService;
    private readonly IEventsService _eventsService;
    private readonly IValuesServiceInternal _valuesService;
    private readonly ITracksUpdateService _tracksUpdateService;
    private readonly IDiagramServiceInternal _diagramService;
    private readonly IDiagramTypeService _diagramTypeService;
    private readonly IRightServiceInternal _rightService;
    private readonly VisualService _visualService;
    private readonly FileService _fileService;

    public MapServiceImpl(
      VmsMapDbContext db,
      IMapService mapService,
      IMapUpdateService mapUpdateService,
      IStatesUpdateService statesUpdateService,
      IStateService stateService,
      IEventsService eventsService,
      IValuesServiceInternal valuesService,
      ITracksUpdateService tracksUpdateService,
      IDiagramServiceInternal diagramService,
      IDiagramTypeService diagramTypeService,
      IRightServiceInternal rightService,
      VisualService visualService,
      FileService fileService)
    {
      _db = db;
      _mapService = mapService;
      _mapUpdateService = mapUpdateService;
      _statesUpdateService = statesUpdateService;
      _stateService = stateService;
      _eventsService = eventsService;
      _valuesService = valuesService;
      _tracksUpdateService = tracksUpdateService;
      _diagramService = diagramService;
      _diagramTypeService = diagramTypeService;
      _rightService = rightService;
      _visualService = visualService;
      _fileService = fileService;
    }

    // ── Objects ───────────────────────────────────────────────────────────────────────────────

    public override async Task<MapProto.ObjectList> UpdateObjects(
      MapProto.ObjectList request, ServerCallContext context)
    {
      var dtos = request.Objects.Select(ToDto).ToList();

      await using var tx = await _db.Database.BeginTransactionAsync(context.CancellationToken);

      var updated = await _mapUpdateService.UpdateObjects(dtos);

      await tx.CommitAsync(context.CancellationToken);

      return new MapProto.ObjectList { Objects = { updated.Select(ToProto) } };
    }

    public override async Task<MapProto.OperationStatus> UpdateProperties(
      MapProto.ObjectPropertiesList request, ServerCallContext context)
    {
      await using var tx = await _db.Database.BeginTransactionAsync(context.CancellationToken);

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

      await using var tx = await _db.Database.BeginTransactionAsync(context.CancellationToken);

      await _mapUpdateService.UpdateFigures(figures);

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
      await using var tx = await _db.Database.BeginTransactionAsync(context.CancellationToken);

      var removed = await _mapUpdateService.Delete(request.Ids.ToList());

      await tx.CommitAsync(context.CancellationToken);

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

      await _eventsService.InsertManyAsync(dtos);
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

      await _diagramService.UpdateListAsync(dtos);

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

      await _diagramTypeService.UpdateListAsync(dtos);

      return new MapProto.DiagramTypeList { DiagramTypes = { dtos.Select(ToProto) } };
    }

    public override async Task<MapProto.OperationStatus> DeleteDiagramTypes(
      MapProto.ObjectIds request, ServerCallContext context)
    {
      await _diagramTypeService.DeleteAsync(request.Ids.ToList());
      return Ok();
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
      file_id = diagram.FileId
    };

    private static MapProto.Diagram ToProto(DiagramDTO dto) => new()
    {
      Id = dto.id ?? string.Empty,
      Geometry = ToProto(dto.geometry),
      RegionId = dto.region_id ?? string.Empty,
      DgrType = dto.dgr_type ?? string.Empty,
      FileId = dto.file_id ?? string.Empty
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
      regions = type.Regions.Select(r => new DiagramTypeRegionDTO
      {
        region_key = r.RegionKey,
        geometry = ToDto(r.Geometry),
        styles = r.Styles.ToDictionary(kv => kv.Key, kv => kv.Value)
      }).ToList()
    };

    private static MapProto.DiagramType ToProto(DiagramTypeDTO dto) => new()
    {
      Id = dto.id ?? string.Empty,
      Name = dto.name ?? string.Empty,
      FileId = dto.file_id ?? string.Empty,
      Regions =
      {
        (dto.regions ?? new List<DiagramTypeRegionDTO>()).Select(r => new MapProto.DiagramTypeRegion
        {
          RegionKey = r.region_key ?? string.Empty,
          Geometry = ToProto(r.geometry) ?? new MapProto.DiagramCoordinate(),
          Styles = { r.styles ?? new Dictionary<string, string>() }
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
