using System.Security.Cryptography;
using System.Text;
using Dapr.Client;
using Google.Protobuf.WellKnownTypes;
using LeafletAlarmsGrpc;

namespace SquareIntegrationClient;

public class SquareIntegrationGrpcClient : ISquareIntegration
{
  private readonly TreeAlarmsGrpcService.TreeAlarmsGrpcServiceClient _tracksClient;
  private readonly IntegroService.IntegroServiceClient _integroClient;
  private readonly string _appId;

  public bool Enabled => true;

  public SquareIntegrationGrpcClient(string sinkAppId, string appId)
  {
    _appId = appId;

    var invoker = DaprClient.CreateInvocationInvoker(sinkAppId);
    _tracksClient = new TreeAlarmsGrpcService.TreeAlarmsGrpcServiceClient(invoker);
    _integroClient = new IntegroService.IntegroServiceClient(invoker);
  }

  // Square requires object_id to parse as a GUID (Square/Domain/Utils.cs ConvertObjectIdToGuid
  // silently drops anything else). vms_rec's camera_id is already a GUID in practice (generated
  // via crypto.randomUUID() when a camera is created, see CameraSetupPanel.tsx) — using it
  // directly means the same id identifies the camera in both systems, no secondary lookup needed.
  // The MD5-derived fallback only covers hypothetical non-GUID ids (e.g. hand-edited data); for
  // those, Square's object_id won't equal the native id, since a hash isn't reversible.
  private static string ToObjectId(string nativeId)
  {
    if (Guid.TryParse(nativeId, out var guid))
    {
      return guid.ToString();
    }

    var hash = MD5.HashData(Encoding.UTF8.GetBytes(nativeId));
    return new Guid(hash).ToString();
  }

  private static ProtoObjExtraProperty TextProp(string name, string value) =>
    new() { PropName = name, StrVal = value, VisualType = "" };

  public async Task RegisterRoot(string appId, string name, double lat, double lon)
  {
    var rootId = ToObjectId(appId);
    await _integroClient.UpdateIntegroAsync(new IntegroListProto
    {
      Objects = { new IntegroProto { ObjectId = rootId, IName = appId, IType = "main" } }
    });

    // UpdateIntegro above only tags rootId with producer metadata (i_name/i_type) — it does NOT
    // create an actual tree/map object. Without this UpdateFigures call, cameras whose ParentId
    // points at rootId are orphaned: the parent never exists as a real object, so they have no
    // reachable path from Square's top-level (parent_id=null) tree view, even though the camera
    // objects themselves were created fine and are queryable directly by id.
    var rootFig = new ProtoFig
    {
      Id = rootId,
      Name = name,
      Geometry = new Common.ProtoGeometry
      {
        Type = "Point",
        Coord = { new Common.ProtoCoord { Lat = lat, Lon = lon } },
      },
    };
    await _tracksClient.UpdateFiguresAsync(new ProtoFigures { Figs = { rootFig }, AddTracks = false });
  }

  public async Task UpsertCameraFigure(
    string cameraId, string name, string parentId, double lat, double lon,
    IReadOnlyDictionary<string, string> props)
  {
    var objectId = ToObjectId(cameraId);
    var parentObjectId = ToObjectId(parentId);

    await _integroClient.UpdateIntegroAsync(new IntegroListProto
    {
      Objects = { new IntegroProto { ObjectId = objectId, IName = _appId, IType = "camera" } }
    });

    var fig = new ProtoFig
    {
      Id = objectId,
      ParentId = parentObjectId,
      Name = name,
      Geometry = new Common.ProtoGeometry
      {
        Type = "Point",
        Coord = { new Common.ProtoCoord { Lat = lat, Lon = lon } },
      },
    };
    foreach (var (key, value) in props)
    {
      fig.ExtraProps.Add(TextProp(key, value));
    }

    await _tracksClient.UpdateFiguresAsync(new ProtoFigures { Figs = { fig }, AddTracks = false });
  }

  public async Task PushCameraProperties(string cameraId, IReadOnlyDictionary<string, string> props)
  {
    var objectId = ToObjectId(cameraId);
    var objProps = new ProtoObjProps { Id = objectId };
    foreach (var (key, value) in props)
    {
      objProps.Properties.Add(TextProp(key, value));
    }

    await _tracksClient.UpdatePropertiesAsync(new ProtoObjPropsList
    {
      Objects = { objProps },
      ReplaceProps = false,
    });
  }

  public async Task PushAlarmEvent(string cameraId, DateTime alarmStartTs, string eventName, string? streamName)
  {
    var objectId = ToObjectId(cameraId);

    await _tracksClient.UpdateEventsAsync(new EventsProto
    {
      Events =
      {
        new EventProto
        {
          Id = Guid.NewGuid().ToString(),
          ObjectId = objectId,
          Timestamp = Timestamp.FromDateTime(DateTime.SpecifyKind(alarmStartTs, DateTimeKind.Utc)),
          EventName = eventName,
          EventPriority = 0,
          Param0 = streamName ?? "",
          Param1 = "",
        },
      },
    });

    await PushCameraProperties(cameraId, new Dictionary<string, string>
    {
      ["last_alarm_ts"] = alarmStartTs.ToString("O"),
    });
  }
}
