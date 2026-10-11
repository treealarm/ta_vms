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
  // every caller already has it in hand (CameraChangeSubscriber has just loaded it to get here;
  // PushAllCamerasAsync's resync loop gets it in bulk from ListCamerasAsync), so an internal
  // re-fetch here was a redundant DB round-trip per camera.
  //
  // Who owns the camera's position decides what is sent (docs/maps-and-plans.md). A camera whose
  // reference placement is in a ta_vms space with real coordinates owns it: its point and heading go
  // up with every push (see PositionFor). Any other camera leaves it to Square — it is given a
  // default point once, when Square first learns of it, because Square creates nothing without
  // geometry; after that only its name and properties are sent, so wherever someone dragged it in
  // Square it stays.
  /// <summary>The placement whose position Square should show, if ta_vms owns one.</summary>
  /// <remarks>
  /// Only the reference placement, and only in real coordinates: a picture of the camera on some
  /// other plan, or its true place on a plan in made-up coordinates, is no point on Square's map.
  /// </remarks>
  public static CameraPlacement? PositionFor(CameraPlacement? reference, Space? space) =>
    reference is { IsReference: true } && space is { RealCoordinates: true } ? reference : null;

  public static async Task PushCameraAsync(
    ISquareIntegration square, CameraSetup setup, CameraPlacement? placement, bool knownToSquare, string producerId)
  {
    // Playback details (webrtc host/port, stream names) are NOT pushed here anymore — Square's
    // Monitor tab fetches them directly from ta_vms's own GET /api/live/cameras (CORS+JWT already
    // enabled for that), so any future field is available there for free with no push code on
    // either side. Only what the map's own property panel needs stays here.
    var primaryStream = setup.Streams.FirstOrDefault(s => s.WebRtc);
    var profileProp = TextProp("profile_name", primaryStream?.RecordingProfile?.Name ?? "");

    var objectId = ToObjectId(setup.CameraId);
    var parentObjectId = ToObjectId(producerId);

    if (placement is null && knownToSquare)
    {
      await Task.WhenAll(
        square.UpsertObjects(new ProtoObjectList
        {
          Objects = { new ProtoObject { Id = objectId, ParentId = parentObjectId, Name = setup.Name } },
        }),
        square.PushProperties(new ProtoObjPropsList
        {
          Objects = { new ProtoObjProps { Id = objectId, Properties = { profileProp } } },
          ReplaceProps = false,
        }));
      return;
    }

    var fig = new ProtoFig
    {
      Id = objectId,
      ParentId = parentObjectId,
      Name = setup.Name,
      Geometry = new Common.ProtoGeometry
      {
        Type = "Point",
        Coord =
        {
          placement is null
            ? new Common.ProtoCoord { Lat = DefaultLat, Lon = DefaultLon }
            : new Common.ProtoCoord { Lat = placement.Lat, Lon = placement.Lon },
        },
      },
      ExtraProps = { profileProp },
    };
    // Square draws an icon turned by __image_rotate, a compass bearing like our heading.
    if (placement is not null)
      fig.ExtraProps.Add(TextProp("__image_rotate",
        placement.HeadingDeg.ToString(System.Globalization.CultureInfo.InvariantCulture)));

    // RegisterIntegro and PushFigures are independent calls (neither depends on the other's
    // result) — running them concurrently halves the round-trip latency per camera.
    await Task.WhenAll(
      square.RegisterIntegro(new IntegroListProto
      {
        Objects = { new IntegroProto { ObjectId = objectId, IType = "camera" } }
      }),
      square.PushFigures(new ProtoFigures { Figs = { fig }, AddTracks = false }));
  }

  /// Which of these cameras Square already holds an object for. Asked only about cameras that are
  /// not placed — a placed one sends its geometry either way.
  public static async Task<HashSet<string>> KnownToSquareAsync(
    ISquareIntegration square, IEnumerable<string> cameraIds)
  {
    var ids = cameraIds.Select(ToObjectId).ToList();
    if (ids.Count == 0)
      return [];
    var objects = await square.GetObjects(ids) ?? [];
    return objects.Select(o => o.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
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
    var spaces = (await cfgClient.ListSpacesAsync(new Google.Protobuf.WellKnownTypes.Empty(), cancellationToken: ct))
      .Spaces.ToDictionary(s => s.Id, StringComparer.Ordinal);
    var placements = (await cfgClient.ListCameraPlacementsAsync(new Google.Protobuf.WellKnownTypes.Empty(), cancellationToken: ct))
      .Placements
      .Select(p => PositionFor(p, spaces.GetValueOrDefault(p.SpaceId)))
      .OfType<CameraPlacement>()
      .ToDictionary(p => p.CameraId, StringComparer.Ordinal);
    var known = await KnownToSquareAsync(
      square, cameras.Cameras.Where(c => !placements.ContainsKey(c.CameraId)).Select(c => c.CameraId));

    foreach (var setup in cameras.Cameras)
    {
      // One camera's failure must not abort the rest of the batch — otherwise a single
      // persistently-broken camera blocks every camera after it in iteration order, on every
      // resync attempt (this is what silently dropped pushes for camera ids later in the list).
      try
      {
        await PushCameraAsync(
          square, setup, placements.GetValueOrDefault(setup.CameraId),
          known.Contains(ToObjectId(setup.CameraId)), producerId);
      }
      catch (Exception ex)
      {
        logger.LogWarning(ex, "Square push failed for camera '{CameraId}'", setup.CameraId);
      }
    }
  }
}
