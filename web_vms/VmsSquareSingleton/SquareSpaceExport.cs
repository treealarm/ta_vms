using System.Security.Cryptography;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using LeafletAlarmsGrpc;
using SquareIntegrationClient;
using Vms.Cfg;

namespace VmsSquareSingleton;

/// <summary>
/// Puts ta_vms's spaces into Square as its diagrams: each space an object in the tree, each plan a
/// diagram with its picture, each camera drawn where it stands and, as an object Square calls a
/// replica (<c>owner_id</c> = the camera), on every other plan it is pictured on. See
/// docs/maps-and-plans.md, "Export to Square".
/// </summary>
/// <remarks>
/// <para>
/// One full export each time rather than per change: there are tens of spaces, not thousands, and
/// a whole picture is what keeps a parent's diagram right when a child's footprint moves. What was
/// sent last is remembered, so a change that alters nothing Square shows costs one read of VmsCfg.
/// </para>
/// <para>
/// Square's integration protocol cannot delete. A space removed here stays in Square, as a camera
/// removed here does, until the protocol can say so.
/// </para>
/// </remarks>
public sealed class SquareSpaceExport(
  CfgService.CfgServiceClient cfg,
  ISquareIntegration square,
  SquareProducerId producerId,
  ILogger<SquareSpaceExport> logger)
{
  /// <summary>The diagram type the camera icons are drawn with: Square's own camera picture.</summary>
  public const string CameraDiagramType = "vms-camera";
  private const string CameraIconSrc = "images/videocamera.png";

  /// <summary>Where uploaded plan pictures live in Square, under its static files.</summary>
  private const string StaticFolder = "static_files";

  // Pictures already handed to Square, by space and etag: an unchanged picture is not sent again.
  private readonly Dictionary<string, string> _uploaded = new(StringComparer.Ordinal);
  private string? _lastFingerprint;
  // The resync and a change can both ask at once; one export at a time keeps the memory above true.
  private readonly SemaphoreSlim _gate = new(1, 1);

  public async Task ExportAsync(CancellationToken ct)
  {
    await _gate.WaitAsync(ct);
    try
    {
      await ExportLockedAsync(ct);
    }
    finally
    {
      _gate.Release();
    }
  }

  private async Task ExportLockedAsync(CancellationToken ct)
  {
    var spaces = (await cfg.ListSpacesAsync(new Empty(), cancellationToken: ct)).Spaces.ToList();
    var placements = (await cfg.ListCameraPlacementsAsync(new Empty(), cancellationToken: ct)).Placements.ToList();
    var names = (await cfg.ListCamerasAsync(new ListCamerasRequest(), cancellationToken: ct)).Cameras
      .ToDictionary(c => c.CameraId, c => c.Name, StringComparer.Ordinal);

    var backgrounds = new Dictionary<string, string>(StringComparer.Ordinal);
    foreach (var space in spaces.Where(s => s.Plan is { Etag.Length: > 0 }))
    {
      try
      {
        backgrounds[space.Id] = await EnsureUploadedAsync(space, ct);
      }
      catch (Exception ex) when (ex is not OperationCanceledException)
      {
        // The diagram still goes, without its picture; the next export tries the picture again.
        logger.LogWarning(ex, "Square: uploading the picture of space '{Space}' failed", space.Name);
      }
    }

    var batch = Build(spaces, placements, names, backgrounds, producerId.Value);
    var fingerprint = Fingerprint(batch);
    if (fingerprint == _lastFingerprint)
      return;

    // Figures first and the tree last: a figure carries no owner, and whatever writes the tree
    // after it decides what a replica's owner_id is.
    await square.PushDiagramTypes(batch.DiagramTypes);
    await square.PushFigures(batch.Figures);
    await square.PushDiagrams(batch.Diagrams);
    await square.UpsertObjects(batch.Objects);
    await square.RegisterIntegro(batch.Integros);
    _lastFingerprint = fingerprint;
    logger.LogInformation(
      "Square: exported {Spaces} spaces, {Diagrams} diagram entries, {Figures} figures",
      spaces.Count, batch.Diagrams.Diagrams.Count, batch.Figures.Figs.Count);
  }

  /// <summary>Everything one export sends, built without Square so it can be tested.</summary>
  public sealed record Batch(
    ProtoObjectList Objects,
    ProtoFigures Figures,
    DiagramTypesProto DiagramTypes,
    DiagramsProto Diagrams,
    IntegroListProto Integros);

  /// <param name="backgrounds">The Square address of each plan picture that is there, by space id.</param>
  public static Batch Build(
    IReadOnlyList<Space> spaces,
    IReadOnlyList<CameraPlacement> placements,
    IReadOnlyDictionary<string, string> cameraNames,
    IReadOnlyDictionary<string, string> backgrounds,
    string producerId)
  {
    var root = SquareCameraPush.ToObjectId(producerId);
    var byId = spaces.ToDictionary(s => s.Id, StringComparer.Ordinal);
    var frames = SquareSpaceGeometry.BuildFrames(spaces);

    var objects = new ProtoObjectList();
    var figures = new ProtoFigures { AddTracks = false };
    var diagrams = new DiagramsProto();
    var integros = new IntegroListProto();
    var types = new DiagramTypesProto
    {
      DiagramTypes =
      {
        new DiagramTypeProto
        {
          Id = SquareCameraPush.ToObjectId($"{producerId}/{CameraDiagramType}"),
          Name = CameraDiagramType,
          Src = CameraIconSrc,
        },
      },
    };

    // Parents before children, so Square never meets a parent it does not know yet.
    foreach (var space in TopDown(spaces))
    {
      var id = SquareCameraPush.ToObjectId(space.Id);
      var parent = byId.GetValueOrDefault(space.ParentId ?? string.Empty);
      objects.Objects.Add(new ProtoObject
      {
        Id = id,
        ParentId = parent is null ? root : SquareCameraPush.ToObjectId(parent.Id),
        Name = space.Name,
      });
      integros.Objects.Add(new IntegroProto { ObjectId = id, IType = "space" });

      if (frames.TryGetValue(space.Id, out var frame))
      {
        diagrams.Diagrams.Add(new DiagramProto
        {
          Id = id,
          Geometry = Coord(frame.Geometry),
          BackgroundImg = backgrounds.GetValueOrDefault(space.Id, string.Empty),
        });
      }

      // A plan inside a map is reached from its outline on Square's map: the footprint is in the
      // map's own, real coordinates.
      if (parent is { Kind: SpaceKind.Map } && space.Footprint.Count >= 3)
      {
        var outline = new Common.ProtoGeometry { Type = "Polygon" };
        outline.Coord.AddRange(space.Footprint.Select(p => new Common.ProtoCoord { Lat = p.Lat, Lon = p.Lon }));
        figures.Figs.Add(new ProtoFig { Id = id, ParentId = SquareCameraPush.ToObjectId(parent.Id), Name = space.Name, Geometry = outline });
      }
    }

    foreach (var p in placements)
    {
      if (!byId.TryGetValue(p.SpaceId, out var space))
        continue;
      var spaceObject = SquareCameraPush.ToObjectId(space.Id);
      var name = cameraNames.GetValueOrDefault(p.CameraId, p.CameraId);

      // Where the camera really stands is the camera object itself (its parent and its point on
      // the map are pushed with it, SquareCameraPush); a picture is a replica owned by it.
      var id = p.IsReference ? SquareCameraPush.ToObjectId(p.CameraId) : PictureId(p);
      if (!p.IsReference)
      {
        objects.Objects.Add(new ProtoObject
        {
          Id = id, ParentId = spaceObject, OwnerId = SquareCameraPush.ToObjectId(p.CameraId), Name = name,
        });
        integros.Objects.Add(new IntegroProto { ObjectId = id, IType = "camera_picture" });
      }

      if (frames.TryGetValue(space.Id, out var frame))
      {
        diagrams.Diagrams.Add(new DiagramProto
        {
          Id = id,
          Geometry = Coord(SquareSpaceGeometry.IconAt(frame, p.Lat, p.Lon)),
          DgrType = CameraDiagramType,
        });
      }
      else if (!p.IsReference)
      {
        // A picture on a map is a point there; the reference's point is the camera's own push.
        figures.Figs.Add(new ProtoFig
        {
          Id = id,
          ParentId = spaceObject,
          Name = name,
          Geometry = new Common.ProtoGeometry { Type = "Point", Coord = { new Common.ProtoCoord { Lat = p.Lat, Lon = p.Lon } } },
        });
      }
    }

    return new Batch(objects, figures, types, diagrams, integros);
  }

  /// <summary>The replica of a camera on one space: the same id every time.</summary>
  public static string PictureId(CameraPlacement p) => SquareCameraPush.ToObjectId($"{p.CameraId}/{p.SpaceId}");

  private static DiagramCoordProto Coord(DiagramRect r) =>
    new() { Left = r.Left, Top = r.Top, Width = r.Width, Height = r.Height };

  private static IEnumerable<Space> TopDown(IReadOnlyList<Space> spaces)
  {
    var children = spaces.ToLookup(s => s.ParentId ?? string.Empty, StringComparer.Ordinal);
    var ids = spaces.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
    var queue = new Queue<Space>(spaces.Where(s => string.IsNullOrEmpty(s.ParentId) || !ids.Contains(s.ParentId)));
    var seen = new HashSet<string>(StringComparer.Ordinal);
    while (queue.Count > 0)
    {
      var s = queue.Dequeue();
      if (!seen.Add(s.Id))
        continue;
      yield return s;
      foreach (var c in children[s.Id])
        queue.Enqueue(c);
    }
  }

  /// <summary>Hands a plan's picture to Square once per version, and says where it now lives.</summary>
  private async Task<string> EnsureUploadedAsync(Space space, CancellationToken ct)
  {
    var plan = space.Plan;
    var extension = plan.Mime switch
    {
      "image/png" => "png",
      "image/jpeg" => "jpg",
      "image/webp" => "webp",
      "image/svg+xml" => "svg",
      _ => "img",
    };
    // The etag in the name: a new picture is a new file, so no browser shows the old one from cache.
    var path = $"{producerId.Value}/plans";
    var file = $"{space.Id}-{plan.Etag[..Math.Min(12, plan.Etag.Length)]}.{extension}";
    var address = $"{StaticFolder}/{path}/{file}";
    if (_uploaded.GetValueOrDefault(space.Id) == plan.Etag)
      return address;

    using var call = cfg.GetSpacePlanImage(new GetSpacePlanImageRequest { SpaceId = space.Id }, cancellationToken: ct);
    using var image = new MemoryStream();
    await foreach (var chunk in call.ResponseStream.ReadAllAsync(ct))
    {
      if (chunk.PartCase == SpacePlanImageChunk.PartOneofCase.Data)
        chunk.Data.WriteTo(image);
    }

    await square.UploadFile(new UploadFileProto
    {
      MainFolder = StaticFolder,
      Path = path,
      FileName = file,
      FileData = ByteString.CopyFrom(image.ToArray()),
    });
    _uploaded[space.Id] = plan.Etag;
    return address;
  }

  private static string Fingerprint(Batch batch)
  {
    using var sha = SHA256.Create();
    foreach (var message in new IMessage[] { batch.Objects, batch.Figures, batch.DiagramTypes, batch.Diagrams, batch.Integros })
    {
      var bytes = message.ToByteArray();
      sha.TransformBlock(bytes, 0, bytes.Length, null, 0);
    }
    sha.TransformFinalBlock([], 0, 0);
    return Convert.ToHexString(sha.Hash!);
  }
}
