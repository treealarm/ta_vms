using System.Security.Cryptography;
using System.Text;
using LeafletAlarmsGrpc;
using SquareIntegrationClient;
using Vms.Cfg;

namespace VmsSquareSingleton;

// Shared by the change subscribers (per-camera push on config/state change), the startup resync
// (full re-push of every camera) and SquareAlarmSubscriber (alarm events) — kept in one place so
// these don't drift. Builds ta_vms's camera tree on top of the generic ISquareIntegration
// protocol (see SquareIntegrationClient submodule, docs/square-integration-plan.md) — id
// conversion and figure/property shaping are ta_vms-specific, not part of the shared protocol.
public static class SquareCameraPush
{
  public static readonly double DefaultLat =
    double.TryParse(Environment.GetEnvironmentVariable("SQUARE_INTEGRATION_DEFAULT_LAT"), out var lat) ? lat : 0;
  public static readonly double DefaultLon =
    double.TryParse(Environment.GetEnvironmentVariable("SQUARE_INTEGRATION_DEFAULT_LON"), out var lon) ? lon : 0;

  // Square requires object_id to parse as a GUID (Square/Domain/Utils.cs ConvertObjectIdToGuid
  // silently drops anything else). ta_vms's camera_id is already a GUID in practice (generated
  // via crypto.randomUUID() when a camera is created, see CameraSetupPanel.tsx) — using it
  // directly means the same id identifies the camera in both systems, no secondary lookup needed.
  // The MD5-derived fallback only covers hypothetical non-GUID ids (e.g. hand-edited data); for
  // those, Square's object_id won't equal the native id, since a hash isn't reversible.
  public static string ToObjectId(string nativeId)
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

  public static async Task PushPropertiesAsync(
    ISquareIntegration square, string cameraId, IReadOnlyDictionary<string, string> props)
  {
    var objProps = new ProtoObjProps { Id = ToObjectId(cameraId) };
    foreach (var (key, value) in props)
    {
      objProps.Properties.Add(TextProp(key, value));
    }

    await square.PushProperties(new ProtoObjPropsList { Objects = { objProps }, ReplaceProps = false });
  }

  // Takes the already-loaded CameraSetup (with .Streams populated) rather than re-fetching by id —
  // every caller already has it in hand (CfgServiceImpl's config-change handler just loaded it to
  // get here; PushAllCamerasAsync's resync loop gets it in bulk from ListCamerasAsync), so an
  // internal re-fetch here was a redundant DB round-trip per camera.
  public static async Task PushCameraAsync(ISquareIntegration square, CameraSetup setup, string producerId)
  {
    // Playback details (webrtc host/port, stream names) are NOT pushed here anymore — Square's
    // Monitor tab fetches them directly from ta_vms's own GET /api/live/cameras (CORS+JWT already
    // enabled for that), so any future field is available there for free with no push code on
    // either side. Only what the map's own property panel needs stays here.
    var primaryStream = setup.Streams.FirstOrDefault(s => s.WebRtc);

    var objectId = ToObjectId(setup.CameraId);
    var parentObjectId = ToObjectId(producerId);

    var fig = new ProtoFig
    {
      Id = objectId,
      ParentId = parentObjectId,
      Name = setup.Name,
      Geometry = new Common.ProtoGeometry
      {
        Type = "Point",
        Coord = { new Common.ProtoCoord { Lat = DefaultLat, Lon = DefaultLon } },
      },
      ExtraProps = { TextProp("profile_name", primaryStream?.RecordingProfile?.Name ?? "") },
    };

    // RegisterIntegro and PushFigures are independent calls (neither depends on the other's
    // result) — running them concurrently halves the round-trip latency per camera.
    await Task.WhenAll(
      square.RegisterIntegro(new IntegroListProto
      {
        Objects = { new IntegroProto { ObjectId = objectId, IType = "camera" } }
      }),
      square.PushFigures(new ProtoFigures { Figs = { fig }, AddTracks = false }));
  }

  public static async Task PushAllCamerasAsync(
    ISquareIntegration square, CfgService.CfgServiceClient cfgClient, string producerId,
    ILogger logger, CancellationToken ct)
  {
    var rootId = ToObjectId(producerId);

    // RegisterIntegro above only tags rootId with producer metadata (i_name/i_type) — it does NOT
    // create an actual tree/map object. Without the PushFigures call below, cameras whose ParentId
    // points at rootId are orphaned: the parent never exists as a real object, so they have no
    // reachable path from Square's top-level (parent_id=null) tree view, even though the camera
    // objects themselves were created fine and are queryable directly by id.
    var rootFig = new ProtoFig
    {
      Id = rootId,
      Name = producerId,
      Geometry = new Common.ProtoGeometry
      {
        Type = "Point",
        Coord = { new Common.ProtoCoord { Lat = DefaultLat, Lon = DefaultLon } },
      },
    };
    await Task.WhenAll(
      square.RegisterIntegro(new IntegroListProto
      {
        Objects = { new IntegroProto { ObjectId = rootId, IType = "main" } }
      }),
      square.PushFigures(new ProtoFigures { Figs = { rootFig }, AddTracks = false }));

    // An empty server_id asks for every camera of every node — this service is not per node, and
    // the config now comes over gRPC rather than from a database this process no longer owns.
    var cameras = await cfgClient.ListCamerasAsync(
      new ListCamerasRequest(), cancellationToken: ct);

    foreach (var setup in cameras.Cameras)
    {
      // One camera's failure must not abort the rest of the batch — otherwise a single
      // persistently-broken camera blocks every camera after it in iteration order, on every
      // resync attempt (this is what silently dropped pushes for camera ids later in the list).
      try
      {
        await PushCameraAsync(square, setup, producerId);
      }
      catch (Exception ex)
      {
        logger.LogWarning(ex, "Square push failed for camera '{CameraId}'", setup.CameraId);
      }
    }
  }
}
