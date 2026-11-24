using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NetTopologySuite.Geometries;
using VmsMap.Adapter;
using VmsMap.Clients;
using VmsMap.Db;
using VmsMap.Domain;

namespace VmsMap.Tests;

// The lifecycle of a device diagram: created when a template matches and a part is configured,
// rebuilt when either changes, and never guessed when it cannot be decided.
public class DeviceDiagramServiceTests
{
  private const string CameraId = "3f2504e0-4f89-11d3-9a0c-0305e82c3301";
  private const string OtherCameraId = "3f2504e0-4f89-11d3-9a0c-0305e82c3302";
  private const string Manufacturer = "OnvifSim";
  private const string Model = "Simulated fixed camera";
  private const string PtzModel = "Simulated PTZ camera";
  private const string CameraNamePrefix = "Camera ";
  private const string InputToken = "input";
  private const string LampToken = "lamp";
  private const string LampRegionTwo = "lamp-2";
  private const string RearPanelName = "Rear panel";
  private const string FrontPanelName = "Front panel";
  private const string SecondPlanName = "second plan";
  private const string ShownPanelName = "shown panel";
  private const string CanvasName = "cam diagrams";
  private const string LeftRegion = "left";
  private const string RightRegion = "right";
  private const string SituationName = "already here";
  private const string InnerDiagramName = "nested diagram";
  private const string SpareName = "spare panel";
  private const string AnotherCanvasName = "second canvas";
  private const string SensorKind = "sensor";
  private const double PlacementWidth = 70;
  private const double PlacementHeight = 80;

  private sealed record Services(
    VmsMapDbContext Db,
    MapDb Map,
    DeviceDiagramService Diagrams,
    DeviceNodeService Nodes,
    FakeSettings Settings,
    FakeCatalog Catalog);

  /// The canvas every diagram of these tests is drawn on.
  private static readonly Guid Space = Guid.Parse("4b1f0f6c-2a55-4a2c-9a3e-6f1c1e9b7d01");

  private sealed class FakeSettings : IDeviceSettingsSource
  {
    public List<DeviceSettingsInfo> Rows { get; } = [];

    public Task<IReadOnlyList<DeviceSettingsInfo>> ListAsync(string cameraId, CancellationToken ct) =>
      Task.FromResult<IReadOnlyList<DeviceSettingsInfo>>(
        Rows.Where(r => r.CameraId == cameraId).ToList());

    public Task<DeviceSettingsInfo?> FindAsync(
      string cameraId, string kind, string token, CancellationToken ct) =>
      Task.FromResult(Rows.FirstOrDefault(
        r => r.CameraId == cameraId && r.Kind == kind && r.Token == token));

    public Task<IReadOnlyDictionary<string, IReadOnlyList<DeviceSettingsInfo>>> ListManyAsync(
      IReadOnlyCollection<string> cameraIds, CancellationToken ct) =>
      Task.FromResult<IReadOnlyDictionary<string, IReadOnlyList<DeviceSettingsInfo>>>(
        cameraIds.Distinct(StringComparer.Ordinal).ToDictionary(
          id => id,
          id => (IReadOnlyList<DeviceSettingsInfo>)Rows.Where(r => r.CameraId == id).ToList(),
          StringComparer.Ordinal));

    public void Invalidate(string cameraId) { }
  }

  private sealed class FakeCatalog : ICameraCatalog
  {
    /// <summary>
    /// Whether VmsCfg knows a camera — every camera by default. A removal publishes its event after
    /// the camera is gone, so a test can play the gap a bind has to survive.
    /// </summary>
    public Func<string, bool> Known { get; set; } = _ => true;

    public Task<CameraInfo?> GetAsync(string cameraId, CancellationToken ct) =>
      Task.FromResult(Known(cameraId)
        ? new CameraInfo(cameraId, CameraNamePrefix + cameraId, "srv")
        : null);

    public Task<List<CameraInfo>> ListAsync(string? serverId, CancellationToken ct) =>
      Task.FromResult(new List<CameraInfo>());
  }

  private static async Task<Services> CreateAsync()
  {
    var db = new VmsMapDbContext(
      new DbContextOptionsBuilder<VmsMapDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
        .Options);

    var map = new MapDb(db);
    var stateService = new StateService(db);
    var statesUpdate = new StatesUpdateService(stateService, map, db);
    var levels = new LevelService(db);
    var tracks = new TracksUpdateService(map, new TrackService(db, levels), new GeoService(db, levels));
    var updates = new MapUpdateService(db, map, tracks, statesUpdate);
    var geo = new GeoService(db, levels);

    var settings = new FakeSettings();
    var catalog = new FakeCatalog();
    var nodes = new DeviceNodeService(
      db, map, updates, catalog, settings, NullLogger<DeviceNodeService>.Instance);
    var diagramRows = new DiagramService(db);
    var reconciler = new DeviceDiagramReconciler(
      db, map, updates, diagramRows, settings, NullLogger<DeviceDiagramReconciler>.Instance);
    var diagrams = new DeviceDiagramService(db, map, updates, diagramRows, catalog, geo, reconciler);

    // The canvas the diagrams stand on: a diagram is a map object, and one of these tests needs
    // only the space to exist.
    db.Markers.Add(new DBMarker { id = Space, name = "floor" });
    db.Spaces.Add(new DBSpace { object_id = Space, kind = SpaceKinds.Map });
    await db.SaveChangesAsync();

    return new Services(db, map, diagrams, nodes, settings, catalog);
  }

  private static Task SeedCameraAsync(Services s, string cameraId = CameraId) =>
    s.Map.UpdateHierarchyAsync([
      new BaseMarkerDTO { id = CameraObjectId.For(cameraId), name = CameraNamePrefix + cameraId }
    ]);

  private static async Task SeedModelAsync(
    Services s, string cameraId = CameraId, string manufacturer = Manufacturer, string model = Model)
  {
    s.Db.DeviceInfos.Add(new DBDeviceInfo
    {
      source_kind = DeviceObjectId.CameraSource,
      source_id = cameraId,
      manufacturer = manufacturer,
      model = model,
      firmware = "0.1.0",
    });
    await s.Db.SaveChangesAsync();
  }

  /// A part the device declares and the operator configured — the only kind a region may bind to.
  private static async Task<string> SeedPartAsync(
    Services s, string kind, string token, string? name = null, string cameraId = CameraId)
  {
    var id = Guid.Parse(DeviceObjectId.For(DeviceObjectId.CameraSource, cameraId, kind, token));

    await s.Map.UpdateHierarchyAsync([
      new BaseMarkerDTO
      {
        id = Utils.ConvertGuidToObjectId(id),
        parent_id = CameraObjectId.For(cameraId),
        name = $"Camera / {token}",
      }
    ]);

    s.Db.DeviceNodes.Add(new DBDeviceNode
    {
      object_id = id,
      source_kind = DeviceObjectId.CameraSource,
      source_id = cameraId,
      part_kind = kind,
      part_token = token,
    });

    s.Settings.Rows.Add(new DeviceSettingsInfo(
      cameraId, kind, token, name ?? string.Empty, "inherit", null, false));

    await s.Db.SaveChangesAsync();
    return Utils.ConvertGuidToObjectId(id);
  }

  private sealed record TypeSpec(
    string Name = "Camera rear panel",
    bool AutoAttach = true,
    string Manufacturer = Manufacturer,
    string Model = Model);

  /// The type the administrator draws: a picture, and per region the part it stands for.
  private static async Task<Guid> SeedTypeAsync(
    Services s, TypeSpec spec,
    params (string Region, string Kind, string By, string Value)[] bindings)
  {
    var type = new DBDiagramType
    {
      id = Guid.NewGuid(),
      name = spec.Name,
      source_kind = DeviceObjectId.CameraSource,
      device_manufacturer = spec.Manufacturer,
      device_model = spec.Model,
      auto_attach = spec.AutoAttach,
    };

    foreach (var (region, kind, by, value) in bindings)
    {
      type.regions.Add(new DBDiagramTypeRegion
      {
        diagram_type_id = type.id,
        region_key = region,
        geometry = new DBDiagramCoord { width = 10, height = 10 },
      });

      // A region with no kind is a drawing or a caption: it exists and binds nothing.
      if (string.IsNullOrEmpty(kind))
        continue;

      type.parts.Add(new DBDiagramTypePart
      {
        diagram_type_id = type.id,
        region_key = region,
        part_kind = kind,
        match_by = by,
        match_value = value,
      });
    }

    s.Db.DiagramTypes.Add(type);
    await s.Db.SaveChangesAsync();
    return type.id;
  }

  private static Task EnsureAsync(Services s, string cameraId = CameraId) =>
    s.Diagrams.ReconcileDeviceAsync(
      DeviceObjectId.CameraSource, cameraId, IDeviceDiagramService.DefaultSlot, default);

  /// The space this file's diagrams are drawn on by default, at the point its tests click.
  private static readonly DiagramTarget OnTheSpace =
    new(Utils.ConvertGuidToObjectId(Space), Lat: 38.99, Lon: -76.94);

  /// Draws the device on the tests' space; without a type the one its model names is taken.
  private static Task<string?> AttachAsync(
    Services s, string? typeId = null, string cameraId = CameraId, DiagramTarget? target = null) =>
    s.Diagrams.PlaceDeviceDiagramAsync(
      DeviceObjectId.CameraSource, cameraId, typeId, IDeviceDiagramService.DefaultSlot,
      target ?? OnTheSpace, default);

  /// What the camera's own link does: keeps the diagram with the camera, without naming a canvas.
  private static Task<string?> OpenWithTheCameraAsync(Services s, string? typeId = null) =>
    s.Diagrams.PlaceDeviceDiagramAsync(
      DeviceObjectId.CameraSource, CameraId, typeId, IDeviceDiagramService.DefaultSlot,
      null, default);

  [Fact]
  public async Task ATemplateDrawsTheConfiguredPartsOnItsCanvas()
  {
    var s = await CreateAsync();
    await SeedCameraAsync(s);
    await SeedModelAsync(s);
    await SeedPartAsync(s, DeviceObjectId.DigitalInputPart, InputToken);
    await SeedPartAsync(s, DeviceObjectId.RelayPart, LampToken);
    var typeId = await SeedTypeAsync(s, new TypeSpec(),
      (InputToken, DeviceObjectId.DigitalInputPart, DiagramPartMatch.Token, InputToken),
      (LampToken, DeviceObjectId.RelayPart, DiagramPartMatch.Token, LampToken),
      ("caption", string.Empty, string.Empty, string.Empty));

    await AttachAsync(s);

    var row = await s.Db.DeviceDiagrams.SingleAsync();
    Assert.Equal(typeId, row.diagram_type_id);
    Assert.False(row.auto_created);
    // A diagram is a map object: it stands on the canvas it was drawn on, at the point asked for.
    Assert.Equal(Space, (await s.Db.Markers.SingleAsync(m => m.id == row.object_id)).parent_id);
    Assert.NotNull(await s.Db.GeoObjects.SingleOrDefaultAsync(g => g.id == row.object_id));

    // Two regions bound; the third is a caption and draws nothing.
    var managed = await s.Db.DeviceDiagramParts.ToListAsync();
    Assert.Equal(2, managed.Count);
    Assert.Contains(managed, m => m.region_key == InputToken);
    Assert.Contains(managed, m => m.region_key == LampToken);

    var placements = await s.Db.Diagrams.Where(d => managed.Select(m => m.replica_object_id).Contains(d.id)).ToListAsync();
    Assert.All(placements, p => Assert.NotNull(p.region_id));
    Assert.All(placements, p => Assert.Null(p.geometry));
  }

  [Fact]
  public async Task ASecondRunChangesNothing()
  {
    var s = await CreateAsync();
    await SeedCameraAsync(s);
    await SeedModelAsync(s);
    await SeedPartAsync(s, DeviceObjectId.RelayPart, LampToken);
    await SeedTypeAsync(s, new TypeSpec(), (LampToken, DeviceObjectId.RelayPart, DiagramPartMatch.Token, LampToken));

    await AttachAsync(s);
    var first = (await s.Db.DeviceDiagrams.SingleAsync(), await s.Db.DeviceDiagramParts.SingleAsync());

    await EnsureAsync(s);

    var second = (await s.Db.DeviceDiagrams.SingleAsync(), await s.Db.DeviceDiagramParts.SingleAsync());
    Assert.Equal(first.Item1.object_id, second.Item1.object_id);
    Assert.Equal(first.Item2.replica_object_id, second.Item2.replica_object_id);
  }

  [Fact]
  public async Task ADeviceWithoutConfiguredPartsGetsAnEmptySheet()
  {
    var s = await CreateAsync();
    await SeedCameraAsync(s);
    await SeedModelAsync(s);

    // The device declares the relay, but nobody configured it: no replica, and the region stays as
    // the template drew it.
    var relay = Guid.Parse(DeviceObjectId.For(
      DeviceObjectId.CameraSource, CameraId, DeviceObjectId.RelayPart, LampToken));
    s.Db.DeviceNodes.Add(new DBDeviceNode
    {
      object_id = relay,
      source_kind = DeviceObjectId.CameraSource,
      source_id = CameraId,
      part_kind = DeviceObjectId.RelayPart,
      part_token = LampToken,
    });
    await s.Db.SaveChangesAsync();
    await SeedTypeAsync(s, new TypeSpec(), (LampToken, DeviceObjectId.RelayPart, DiagramPartMatch.Token, LampToken));

    await AttachAsync(s);

    // The sheet is there — the administrator can draw on it — but nothing is bound into it.
    var row = await s.Db.DeviceDiagrams.SingleAsync();
    Assert.NotNull(row.diagram_type_id);
    Assert.Empty(s.Db.DeviceDiagramParts);
  }

  [Fact]
  public async Task ADeviceWithNoTemplateGetsAnEmptySheet()
  {
    var s = await CreateAsync();
    await SeedCameraAsync(s);
    await SeedModelAsync(s);
    await SeedPartAsync(s, DeviceObjectId.RelayPart, LampToken);

    await AttachAsync(s);

    // No template matches the model: a blank sheet rather than nothing at all.
    var row = await s.Db.DeviceDiagrams.SingleAsync();
    Assert.Null(row.diagram_type_id);
    Assert.Empty(s.Db.DeviceDiagramParts);
  }

  [Fact]
  public async Task ADiagramWithoutAPointStandsInTheMiddleOfTheSpace()
  {
    var s = await CreateAsync();
    await SeedCameraAsync(s);
    await SeedModelAsync(s);
    await SeedTypeAsync(s, new TypeSpec(), (LampToken, DeviceObjectId.RelayPart, DiagramPartMatch.Token, LampToken));

    // The camera is not drawn on this canvas, so there is no placement to stand beside.
    var space = await s.Db.Spaces.SingleAsync();
    space.view_south = 38.9; space.view_north = 39.0;
    space.view_west = -77.0; space.view_east = -76.9;
    await s.Db.SaveChangesAsync();

    await AttachAsync(s, target: new DiagramTarget(Utils.ConvertGuidToObjectId(Space)));

    var row = await s.Db.DeviceDiagrams.SingleAsync();
    var figure = await s.Db.GeoObjects.SingleAsync(g => g.id == row.object_id);
    Assert.Equal(38.95, figure.figure!.Centroid.Y, 6);
    Assert.Equal(-76.95, figure.figure!.Centroid.X, 6);
  }

  [Fact]
  public async Task UnconfiguringTheLastPartTakesTheReplicaAway()
  {
    var s = await CreateAsync();
    await SeedCameraAsync(s);
    await SeedModelAsync(s);
    await SeedPartAsync(s, DeviceObjectId.RelayPart, LampToken);
    await SeedTypeAsync(s, new TypeSpec(), (LampToken, DeviceObjectId.RelayPart, DiagramPartMatch.Token, LampToken));

    await AttachAsync(s);
    var replica = (await s.Db.DeviceDiagramParts.SingleAsync()).replica_object_id;

    s.Settings.Rows.Clear();
    await EnsureAsync(s);

    // The diagram stays — the device still has the canvas — but nothing is drawn in it, and the
    // node that used to be is gone. This is the case the first draft of the plan left behind.
    var row = await s.Db.DeviceDiagrams.SingleAsync();
    Assert.Empty(s.Db.DeviceDiagramParts);
    Assert.False(await s.Db.Markers.AnyAsync(m => m.id == replica));
    Assert.True(await s.Db.Markers.AnyAsync(m => m.id == row.object_id));
  }

  [Fact]
  public async Task AReplicaDrawnByHandIsNotTouched()
  {
    var s = await CreateAsync();
    await SeedCameraAsync(s);
    await SeedModelAsync(s);
    var lamp = await SeedPartAsync(s, DeviceObjectId.RelayPart, LampToken);
    await SeedTypeAsync(s, new TypeSpec(), (LampToken, DeviceObjectId.RelayPart, DiagramPartMatch.Token, LampToken));

    await AttachAsync(s);
    var row = await s.Db.DeviceDiagrams.SingleAsync();

    // The administrator puts a second picture of the same part in the same diagram, outside the
    // registry the service keeps.
    var manualId = Guid.NewGuid();
    await s.Map.UpdateHierarchyAsync([
      new BaseMarkerDTO
      {
        id = Utils.ConvertGuidToObjectId(manualId),
        parent_id = Utils.ConvertGuidToObjectId(row.object_id),
        owner_id = lamp,
        name = "lamp (by hand)",
      }
    ]);

    s.Settings.Rows.Clear();
    await EnsureAsync(s);

    Assert.True(await s.Db.Markers.AnyAsync(m => m.id == manualId));
  }

  [Fact]
  public async Task ANewModelRedrawsTheDiagramFromAnotherTemplate()
  {
    var s = await CreateAsync();
    await SeedCameraAsync(s);
    await SeedModelAsync(s, model: Model);
    await SeedPartAsync(s, DeviceObjectId.RelayPart, LampToken);
    await SeedTypeAsync(s, new TypeSpec(Name: RearPanelName),
      (LampToken, DeviceObjectId.RelayPart, DiagramPartMatch.Token, LampToken));
    await SeedTypeAsync(s, new TypeSpec(Name: FrontPanelName, Model: PtzModel),
      (LampToken, DeviceObjectId.RelayPart, DiagramPartMatch.Token, LampToken));

    await AttachAsync(s);
    var node = (await s.Db.DeviceDiagrams.SingleAsync()).object_id;
    var rear = await s.Db.DiagramTypes.SingleAsync(t => t.name == RearPanelName);

    // The device says it is another model now.
    var info = await s.Db.DeviceInfos.SingleAsync();
    info.model = PtzModel;
    await s.Db.SaveChangesAsync();

    await EnsureAsync(s);

    var row = await s.Db.DeviceDiagrams.SingleAsync();
    var front = await s.Db.DiagramTypes.SingleAsync(t => t.name == FrontPanelName);

    // The node stays — its id never depended on the type — and only what is drawn on it changes.
    Assert.Equal(node, row.object_id);
    Assert.NotEqual(rear.id, row.diagram_type_id);
    Assert.Equal(front.id, row.diagram_type_id);
    Assert.Equal(1, await s.Db.DeviceDiagramParts.CountAsync());
  }

  [Fact]
  public async Task ATypeThatNoLongerFitsIsKeptWhenNothingReplacesIt()
  {
    var s = await CreateAsync();
    await SeedCameraAsync(s);
    await SeedModelAsync(s);
    await SeedPartAsync(s, DeviceObjectId.RelayPart, LampToken);
    var typeId = await SeedTypeAsync(s, new TypeSpec(), (LampToken, DeviceObjectId.RelayPart, DiagramPartMatch.Token, LampToken));

    await AttachAsync(s);

    var info = await s.Db.DeviceInfos.SingleAsync();
    info.model = "Something else entirely";
    await s.Db.SaveChangesAsync();

    await EnsureAsync(s);

    // Nothing matches the new model: the diagram stays as it is rather than being taken apart.
    Assert.Equal(typeId, (await s.Db.DeviceDiagrams.SingleAsync()).diagram_type_id);
  }

  [Fact]
  public async Task ATypeDeletedAndMadeAgainIsTakenUpAgain()
  {
    var s = await CreateAsync();
    await SeedCameraAsync(s);
    await SeedModelAsync(s);
    await SeedPartAsync(s, DeviceObjectId.RelayPart, LampToken);
    await SeedTypeAsync(s, new TypeSpec(),
      (LampToken, DeviceObjectId.RelayPart, DiagramPartMatch.Token, LampToken));

    await AttachAsync(s);
    var nodeId = (await s.Db.DeviceDiagrams.SingleAsync()).object_id;

    // Deleting a type leaves the row and the canvas without one (ON DELETE SET NULL) — which is
    // what a demo reinstall does: its templates are removed with the world and made again under
    // new ids on the way back in.
    s.Db.DiagramTypes.Remove(await s.Db.DiagramTypes.SingleAsync());
    (await s.Db.DeviceDiagrams.SingleAsync()).diagram_type_id = null;
    (await s.Db.Diagrams.SingleAsync(d => d.id == nodeId)).dgr_type = null;
    await s.Db.SaveChangesAsync();

    // Nothing fits the device: an untethered diagram is settled and stays as it is.
    await EnsureAsync(s);
    Assert.Null((await s.Db.DeviceDiagrams.SingleAsync()).diagram_type_id);

    // The template comes back under a new id, and is taken up again — on the canvas too, which is
    // what is drawn: a row naming a template the canvas does not is blank paper.
    var again = await SeedTypeAsync(s, new TypeSpec(),
      (LampToken, DeviceObjectId.RelayPart, DiagramPartMatch.Token, LampToken));
    await EnsureAsync(s);

    var row = await s.Db.DeviceDiagrams.SingleAsync();
    Assert.Equal(nodeId, row.object_id);
    Assert.Equal(again, row.diagram_type_id);
    Assert.Equal(again, (await s.Db.Diagrams.SingleAsync(d => d.id == nodeId)).dgr_type);
  }

  [Fact]
  public async Task ASheepLeftBlankTakesItsTemplateOnceThereIsOne()
  {
    var s = await CreateAsync();
    await SeedCameraAsync(s);
    await SeedModelAsync(s);
    await SeedPartAsync(s, DeviceObjectId.RelayPart, LampToken);

    await AttachAsync(s);
    Assert.Null((await s.Db.DeviceDiagrams.SingleAsync()).diagram_type_id);

    // The template is added later; the same button fills the sheet the administrator placed.
    var typeId = await SeedTypeAsync(s, new TypeSpec(),
      (LampToken, DeviceObjectId.RelayPart, DiagramPartMatch.Token, LampToken));
    await AttachAsync(s);

    var row = await s.Db.DeviceDiagrams.SingleAsync();
    Assert.Equal(typeId, row.diagram_type_id);
    Assert.Equal(1, await s.Db.DeviceDiagramParts.CountAsync());
  }

  /// <summary>
  /// A picture of the camera on the tests' canvas: the node the plan draws for it, and the placement
  /// row behind it. A diagram of the device is hung under this node, not under the camera's
  /// canonical one — that one is hardware, and the diagram is what the plan shows of it.
  /// </summary>
  private static async Task<Guid> SeedPictureAsync(Services s) => await SeedPictureAsync(s, Space, true);

  private static async Task<Guid> SeedPictureAsync(
    Services s, Guid spaceId, bool reference, string cameraId = CameraId)
  {
    var picture = Guid.NewGuid();
    await s.Map.UpdateHierarchyAsync([
      new BaseMarkerDTO
      {
        id = Utils.ConvertGuidToObjectId(picture),
        parent_id = Utils.ConvertGuidToObjectId(spaceId),
        owner_id = CameraObjectId.For(cameraId),
        name = CameraNamePrefix + cameraId,
      }
    ]);
    s.Db.CameraPlacements.Add(new DBCameraPlacement
    {
      object_id = picture,
      camera_id = cameraId,
      is_reference = reference,
    });
    await s.Db.SaveChangesAsync();
    return picture;
  }

  /// A second canvas, beside the one every other test draws on.
  private static async Task<Guid> SeedSpaceAsync(Services s, string name)
  {
    var id = Guid.NewGuid();
    s.Db.Markers.Add(new DBMarker { id = id, name = name });
    s.Db.Spaces.Add(new DBSpace { object_id = id, kind = SpaceKinds.Map });
    await s.Db.SaveChangesAsync();
    return id;
  }

  /// <summary>
  /// A mnemoscheme: a diagram node drawn from a type of its own, with the regions the tests place a
  /// device in. The type is what says which keys are places at all.
  /// </summary>
  private sealed record Mnemoscheme(Guid Node, Guid Type);

  private static async Task<Mnemoscheme> SeedMnemoschemeAsync(
    Services s, string name, params string[] regions)
  {
    var id = Guid.NewGuid();
    await s.Map.UpdateHierarchyAsync([
      new BaseMarkerDTO { id = Utils.ConvertGuidToObjectId(id), name = name }
    ]);

    var type = new DBDiagramType { id = Guid.NewGuid(), name = name };
    foreach (var key in regions)
    {
      type.regions.Add(new DBDiagramTypeRegion
      {
        diagram_type_id = type.id,
        region_key = key,
        geometry = new DBDiagramCoord { width = 0.5, height = 0.5 },
      });
    }

    s.Db.DiagramTypes.Add(type);
    s.Db.Diagrams.Add(new DBDiagram { id = id, dgr_type = type.id });
    await s.Db.SaveChangesAsync();
    return new Mnemoscheme(id, type.id);
  }

  /// The camera on a plan, with a configured part and a matching template: what every placement
  /// test starts from.
  private static async Task<Services> PlacedCameraAsync()
  {
    var s = await CreateAsync();
    await SeedCameraAsync(s);
    await SeedModelAsync(s);
    await SeedPartAsync(s, DeviceObjectId.RelayPart, LampToken);
    await SeedTypeAsync(
      s, new TypeSpec(), (LampToken, DeviceObjectId.RelayPart, DiagramPartMatch.Token, LampToken));
    return s;
  }

  [Fact]
  public async Task ARegionOnAMnemoschemeShowsThePanelsDiagramThere()
  {
    var s = await PlacedCameraAsync();
    var picture = await SeedPictureAsync(s);
    var canvas = await SeedMnemoschemeAsync(s, CanvasName, LeftRegion, RightRegion);

    var shownId = await AttachAsync(
      s, target: new DiagramTarget(canvas.Node.ToString(), RegionId: LeftRegion));

    var canonical = await s.Db.DeviceDiagrams.SingleAsync();
    var shown = await s.Db.Markers.SingleAsync(m => m.id == Guid.Parse(shownId!));
    // A representation: it stands on the canvas and shows the diagram, it is not the diagram.
    Assert.Equal(canvas.Node, shown.parent_id);
    Assert.Equal(canonical.object_id, shown.owner_id);

    var row = await s.Db.Diagrams.SingleAsync(d => d.id == shown.id);
    Assert.Equal(LeftRegion, row.region_id);
    Assert.Null(row.geometry);
    Assert.Null(row.dgr_type);
    Assert.Null(row.file_id);

    // The diagram's own card is taken off the plan: shown on a canvas is not also drawn beside the
    // camera, and the point it had there is not a second place.
    Assert.False(await s.Db.GeoObjects.AnyAsync(g => g.id == canonical.object_id));
    // The canonical node itself stays with the camera, where the reader's link finds it.
    Assert.Equal(picture, (await s.Db.Markers.SingleAsync(m => m.id == canonical.object_id)).parent_id);
  }

  [Fact]
  public async Task ARectangleOfItsOwnIsWhereTheRepresentationStands()
  {
    var s = await PlacedCameraAsync();
    await SeedPictureAsync(s);
    var canvas = await SeedMnemoschemeAsync(s, CanvasName, LeftRegion, RightRegion);
    var rect = new DiagramCoordDTO
    {
      left = 40, top = 50, width = PlacementWidth, height = PlacementHeight,
    };

    var shownId = await AttachAsync(
      s, target: new DiagramTarget(canvas.Node.ToString(), Geometry: rect));

    var row = await s.Db.Diagrams.SingleAsync(
      d => d.id == Guid.Parse(shownId!));
    Assert.Null(row.region_id);
    Assert.Equal(PlacementWidth, row.geometry!.width);
    Assert.Equal(PlacementHeight, row.geometry.height);
    Assert.Equal(40, row.geometry.left);
  }

  [Fact]
  public async Task TheSameDiagramInTheSameRegionIsTheSameRepresentation()
  {
    var s = await PlacedCameraAsync();
    await SeedPictureAsync(s);
    var canvas = await SeedMnemoschemeAsync(s, CanvasName, LeftRegion);

    var first = await AttachAsync(
      s, target: new DiagramTarget(canvas.Node.ToString(), RegionId: LeftRegion));
    var second = await AttachAsync(
      s, target: new DiagramTarget(canvas.Node.ToString(), RegionId: LeftRegion));

    // Asking again is not a copy of a copy: the region already shows this diagram.
    Assert.Equal(first, second);
    var canonical = await s.Db.DeviceDiagrams.SingleAsync();
    Assert.Equal(
      1, await s.Db.Markers.CountAsync(m => m.parent_id == canvas.Node && m.owner_id == canonical.object_id));
  }

  [Fact]
  public async Task TwoRegionsOfOneMnemoschemeHoldTwoRepresentations()
  {
    var s = await PlacedCameraAsync();
    await SeedPictureAsync(s);
    var canvas = await SeedMnemoschemeAsync(s, CanvasName, LeftRegion, RightRegion);

    var left = await AttachAsync(
      s, target: new DiagramTarget(canvas.Node.ToString(), RegionId: LeftRegion));
    var right = await AttachAsync(
      s, target: new DiagramTarget(canvas.Node.ToString(), RegionId: RightRegion));

    Assert.NotEqual(left, right);
    var canonical = await s.Db.DeviceDiagrams.SingleAsync();
    Assert.Equal(
      2, await s.Db.Markers.CountAsync(m => m.parent_id == canvas.Node && m.owner_id == canonical.object_id));
  }

  [Fact]
  public async Task OneDiagramIsShownOnAsManyMnemoschemesAsAskForIt()
  {
    var s = await PlacedCameraAsync();
    await SeedPictureAsync(s);
    var first = await SeedMnemoschemeAsync(s, CanvasName, LeftRegion);
    var second = await SeedMnemoschemeAsync(s, SecondPlanName, LeftRegion);

    await AttachAsync(s, target: new DiagramTarget(first.Node.ToString(), RegionId: LeftRegion));
    await AttachAsync(s, target: new DiagramTarget(second.Node.ToString(), RegionId: LeftRegion));

    // One diagram, two places: the copies belong to the same canonical node.
    var canonical = await s.Db.DeviceDiagrams.SingleAsync();
    var copies = await s.Db.Markers
      .Where(m => m.owner_id == canonical.object_id)
      .Select(m => m.parent_id)
      .ToListAsync();
    Assert.Equal(2, copies.Count);
    Assert.Contains(first.Node, copies);
    Assert.Contains(second.Node, copies);
  }

  [Fact]
  public async Task ACopyOfADiagramIsNotACanvas()
  {
    var s = await PlacedCameraAsync();
    await SeedPictureAsync(s);
    var board = await SeedMnemoschemeAsync(s, CanvasName, LeftRegion);

    // A diagram of the administrator's own, and a copy of it standing on the board.
    var drawn = await SeedSpareDiagramAsync(s, SpareName);
    var copy = Guid.NewGuid();
    await s.Map.UpdateHierarchyAsync([
      new BaseMarkerDTO
      {
        id = Utils.ConvertGuidToObjectId(copy),
        parent_id = board.Node.ToString(),
        owner_id = drawn,
        name = ShownPanelName,
      }
    ]);
    s.Db.Diagrams.Add(new DBDiagram { id = copy, region_id = LeftRegion });
    await s.Db.SaveChangesAsync();

    // A copy opens on the diagram it represents: whatever stood under the copy itself would never
    // be read, so the place is refused before anything is written.
    var error = await Assert.ThrowsAsync<DeviceDiagramException>(() => AttachAsync(
      s, target: new DiagramTarget(copy.ToString(), RegionId: LeftRegion)));

    Assert.Contains("representation", error.Message);
    Assert.Empty(await s.Db.DeviceDiagrams.ToListAsync());
  }

  [Fact]
  public async Task ARegionTheCanvasDoesNotHaveIsRefused()
  {
    var s = await PlacedCameraAsync();
    await SeedPictureAsync(s);
    var canvas = await SeedMnemoschemeAsync(s, CanvasName, LeftRegion);

    var error = await Assert.ThrowsAsync<DeviceDiagramException>(() => AttachAsync(
      s, target: new DiagramTarget(canvas.Node.ToString(), RegionId: RightRegion)));

    Assert.Contains(RightRegion, error.Message);
    // A key the type does not offer is a malformed request, not the map's state.
    Assert.False(error.Conflicting);
    Assert.Empty(await s.Db.DeviceDiagrams.ToListAsync());
  }

  [Fact]
  public async Task ARegionBoundToAPartIsRefused()
  {
    var s = await PlacedCameraAsync();
    await SeedPictureAsync(s);
    var canvas = await SeedMnemoschemeAsync(s, CanvasName, LeftRegion);
    s.Db.DiagramTypeParts.Add(new DBDiagramTypePart
    {
      diagram_type_id = canvas.Type,
      region_key = LeftRegion,
      part_kind = DeviceObjectId.RelayPart,
      match_by = DiagramPartMatch.Token,
      match_value = LampToken,
    });
    await s.Db.SaveChangesAsync();

    var error = await Assert.ThrowsAsync<DeviceDiagramException>(() => AttachAsync(
      s, target: new DiagramTarget(canvas.Node.ToString(), RegionId: LeftRegion)));

    // The reconciler's replica stands there: a second node would hide it or be hidden by it.
    Assert.Contains(LeftRegion, error.Message);
    Assert.True(error.Conflicting);
    Assert.Empty(await s.Db.DeviceDiagrams.ToListAsync());
  }

  [Fact]
  public async Task ARegionHoldingAnotherNodeIsRefused()
  {
    var s = await PlacedCameraAsync();
    await SeedPictureAsync(s);
    var canvas = await SeedMnemoschemeAsync(s, CanvasName, LeftRegion);

    var other = Guid.NewGuid();
    await s.Map.UpdateHierarchyAsync([
      new BaseMarkerDTO
      {
        id = Utils.ConvertGuidToObjectId(other),
        parent_id = canvas.Node.ToString(),
        name = SituationName,
      }
    ]);
    s.Db.Diagrams.Add(new DBDiagram { id = other, region_id = LeftRegion });
    await s.Db.SaveChangesAsync();

    var error = await Assert.ThrowsAsync<DeviceDiagramException>(() => AttachAsync(
      s, target: new DiagramTarget(canvas.Node.ToString(), RegionId: LeftRegion)));

    // A region is a slot: one node stands there, and the administrator is told which. The request is
    // well formed — the map's state is what refuses it.
    Assert.Contains(LeftRegion, error.Message);
    Assert.True(error.Conflicting);
    Assert.Empty(await s.Db.DeviceDiagrams.ToListAsync());
  }

  [Fact]
  public async Task ACanvasInsideTheDiagramIsRefusedBeforeAnythingIsWritten()
  {
    var s = await PlacedCameraAsync();
    await SeedPictureAsync(s);
    await AttachAsync(s);
    var canonical = await s.Db.DeviceDiagrams.SingleAsync();

    // The diagram itself is the first canvas that cannot hold it: a copy standing on what it
    // copies would descend into itself on every walk of the tree.
    var itself = await Assert.ThrowsAsync<DeviceDiagramException>(() => AttachAsync(
      s, target: new DiagramTarget(
        Utils.ConvertGuidToObjectId(canonical.object_id)!,
        Geometry: new DiagramCoordDTO { width = 10, height = 10 })));
    Assert.Contains("own canvas", itself.Message);

    // A diagram drawn inside the panel: showing the panel on it would hang the copy under the
    // diagram it copies.
    var inner = Guid.NewGuid();
    await s.Map.UpdateHierarchyAsync([
      new BaseMarkerDTO
      {
        id = Utils.ConvertGuidToObjectId(inner),
        parent_id = Utils.ConvertGuidToObjectId(canonical.object_id),
        name = InnerDiagramName,
      }
    ]);
    s.Db.Diagrams.Add(new DBDiagram { id = inner });
    await s.Db.SaveChangesAsync();

    var error = await Assert.ThrowsAsync<DeviceDiagramException>(() => AttachAsync(
      s, target: new DiagramTarget(
        inner.ToString(), Geometry: new DiagramCoordDTO { width = 10, height = 10 })));

    Assert.Contains("own canvas", error.Message);
    Assert.False(await s.Db.Markers.AnyAsync(m => m.owner_id == canonical.object_id));
  }

  [Fact]
  public async Task TwoCamerasStandInTwoRegionsOfOneMnemoscheme()
  {
    var s = await PlacedCameraAsync();
    await SeedPictureAsync(s);
    await SeedCameraAsync(s, OtherCameraId);
    await SeedModelAsync(s, OtherCameraId);
    await SeedPictureAsync(s, Space, reference: true, cameraId: OtherCameraId);
    var canvas = await SeedMnemoschemeAsync(s, CanvasName, LeftRegion, RightRegion);

    var left = await AttachAsync(
      s, target: new DiagramTarget(canvas.Node.ToString(), RegionId: LeftRegion));
    var right = await AttachAsync(
      s, target: new DiagramTarget(canvas.Node.ToString(), RegionId: RightRegion),
      cameraId: OtherCameraId);

    Assert.NotEqual(left, right);
    var owners = await s.Db.Markers
      .Where(m => m.parent_id == canvas.Node && m.owner_id != null)
      .Select(m => m.owner_id!.Value)
      .Distinct()
      .ToListAsync();
    Assert.Equal(2, owners.Count);
    Assert.Equal(
      LeftRegion,
      (await s.Db.Diagrams.SingleAsync(d => d.id == Guid.Parse(left!))).region_id);
    Assert.Equal(
      RightRegion,
      (await s.Db.Diagrams.SingleAsync(d => d.id == Guid.Parse(right!))).region_id);
  }

  [Fact]
  public async Task ShowingTheDiagramOnACanvasTakesItsCardOffThePlan()
  {
    var s = await PlacedCameraAsync();
    await SeedPictureAsync(s);
    var canvas = await SeedMnemoschemeAsync(s, CanvasName, LeftRegion);

    await AttachAsync(s);
    var canonical = await s.Db.DeviceDiagrams.SingleAsync();
    Assert.True(await s.Db.GeoObjects.AnyAsync(g => g.id == canonical.object_id));

    await AttachAsync(s, target: new DiagramTarget(canvas.Node.ToString(), RegionId: LeftRegion));

    // One diagram, one place: the card beside the camera and the panel on the canvas are the same
    // diagram asked to be shown in two places, and the older one gives way.
    Assert.False(await s.Db.GeoObjects.AnyAsync(g => g.id == canonical.object_id));
    Assert.Equal(
      1, await s.Db.Markers.CountAsync(m => m.owner_id == canonical.object_id));
  }

  [Fact]
  public async Task OpeningTheDiagramFromItsCameraLeavesTheRepresentationWhereItIs()
  {
    var s = await PlacedCameraAsync();
    var picture = await SeedPictureAsync(s);
    var canvas = await SeedMnemoschemeAsync(s, CanvasName, LeftRegion);

    var shownId = await AttachAsync(
      s, target: new DiagramTarget(canvas.Node.ToString(), RegionId: LeftRegion));
    var canonical = await s.Db.DeviceDiagrams.SingleAsync();

    // The camera's own link names no canvas: it keeps the diagram with the camera and must not
    // move or redraw either of the two.
    await OpenWithTheCameraAsync(s);

    Assert.Equal(picture, (await s.Db.Markers.SingleAsync(m => m.id == canonical.object_id)).parent_id);
    Assert.False(await s.Db.GeoObjects.AnyAsync(g => g.id == canonical.object_id));
    Assert.Equal(canvas.Node, (await s.Db.Markers.SingleAsync(m => m.id == Guid.Parse(shownId!))).parent_id);
  }

  [Fact]
  public async Task APlaceThatDoesNotFitTheCanvasKindIsRefused()
  {
    var s = await PlacedCameraAsync();
    await SeedPictureAsync(s);
    var canvas = await SeedMnemoschemeAsync(s, CanvasName, LeftRegion);
    var space = Space.ToString();

    // A space takes a point; a diagram's canvas takes a region or a rectangle.
    await Assert.ThrowsAsync<DeviceDiagramException>(() => AttachAsync(
      s, target: new DiagramTarget(space, RegionId: LeftRegion)));
    await Assert.ThrowsAsync<DeviceDiagramException>(() => AttachAsync(
      s, target: new DiagramTarget(canvas.Node.ToString(), Lat: 1, Lon: 2)));
    await Assert.ThrowsAsync<DeviceDiagramException>(() => AttachAsync(
      s, target: new DiagramTarget(canvas.Node.ToString(), RegionId: LeftRegion,
        Geometry: new DiagramCoordDTO { width = 1, height = 1 })));

    Assert.Empty(await s.Db.DeviceDiagrams.ToListAsync());
  }

  [Fact]
  public async Task ACanvasThatDoesNotExistIsRefused()
  {
    var s = await PlacedCameraAsync();
    await SeedPictureAsync(s);

    await Assert.ThrowsAsync<DeviceDiagramException>(() => AttachAsync(
      s, target: new DiagramTarget(Guid.NewGuid().ToString(), RegionId: LeftRegion)));

    Assert.Empty(await s.Db.DeviceDiagrams.ToListAsync());
  }

  [Fact]
  public async Task TheDiagramHangsUnderTheCamerasPictureOnTheCanvas()
  {
    var s = await CreateAsync();
    await SeedCameraAsync(s);
    await SeedModelAsync(s);
    await SeedPartAsync(s, DeviceObjectId.RelayPart, LampToken);
    await SeedTypeAsync(s, new TypeSpec(), (LampToken, DeviceObjectId.RelayPart, DiagramPartMatch.Token, LampToken));
    var picture = await SeedPictureAsync(s);

    await AttachAsync(s);

    var row = await s.Db.DeviceDiagrams.SingleAsync();
    var node = await s.Db.Markers.SingleAsync(m => m.id == row.object_id);
    Assert.Equal(picture, node.parent_id);

    // Its own point stays on the plan: the ground it is drawn on is the space through the picture,
    // and the point asked for is the point stored.
    var geo = await s.Db.GeoObjects.SingleAsync(g => g.id == row.object_id);
    Assert.Equal(Space, geo.canvas_id);
    Assert.True(geo.figure is Point point
                && Math.Abs(point.Y - 38.99) < 1e-9 && Math.Abs(point.X + 76.94) < 1e-9);
  }

  [Fact]
  public async Task ADiagramWithNoCanvasNamedLivesUnderTheReferencePictureAndIsNotDrawn()
  {
    var s = await CreateAsync();
    await SeedCameraAsync(s);
    await SeedModelAsync(s);
    await SeedPartAsync(s, DeviceObjectId.RelayPart, LampToken);
    await SeedTypeAsync(s, new TypeSpec(), (LampToken, DeviceObjectId.RelayPart, DiagramPartMatch.Token, LampToken));
    var picture = await SeedPictureAsync(s);

    var id = await OpenWithTheCameraAsync(s);

    Assert.NotNull(id);
    var node = await s.Db.Markers.SingleAsync(m => m.id == Guid.Parse(id!));
    Assert.Equal(picture, node.parent_id);
    // Nothing is drawn: the diagram is opened from the camera's link, and a picture beside the
    // camera is a place an administrator asks for.
    Assert.False(await s.Db.GeoObjects.AnyAsync(g => g.id == node.id));
    Assert.Equal(1, await s.Db.DeviceDiagramParts.CountAsync());
  }

  [Fact]
  public async Task ADeviceNobodyPlacedHasNothingToHangItsDiagramUnder()
  {
    var s = await CreateAsync();
    await SeedCameraAsync(s);
    await SeedModelAsync(s);
    await SeedTypeAsync(s, new TypeSpec());

    var id = await OpenWithTheCameraAsync(s);

    Assert.Null(id);
    Assert.Empty(await s.Db.DeviceDiagrams.ToListAsync());
  }

  [Fact]
  public async Task ADiagramBesideTheCameraIsNestedUnderItsPictureAndKeepsItsPoint()
  {
    var s = await CreateAsync();
    await SeedCameraAsync(s);
    await SeedModelAsync(s);
    await SeedPartAsync(s, DeviceObjectId.RelayPart, LampToken);
    await SeedTypeAsync(s, new TypeSpec(), (LampToken, DeviceObjectId.RelayPart, DiagramPartMatch.Token, LampToken));

    // A diagram of the older world: it stands beside the camera as a child of the space.
    await AttachAsync(s);
    var objectGuid = (await s.Db.DeviceDiagrams.SingleAsync()).object_id;
    Assert.Equal(Space, (await s.Db.Markers.SingleAsync(m => m.id == objectGuid)).parent_id);

    // The camera's picture appears, and the button is pressed again from the canvas without a point:
    // the diagram is nested under the picture, and the point it was dragged to stays.
    var picture = await SeedPictureAsync(s);
    await AttachAsync(s, target: new DiagramTarget(Utils.ConvertGuidToObjectId(Space)));

    Assert.Equal(picture, (await s.Db.Markers.SingleAsync(m => m.id == objectGuid)).parent_id);
    var geo = await s.Db.GeoObjects.SingleAsync(g => g.id == objectGuid);
    Assert.True(geo.figure is Point point
                && Math.Abs(point.Y - 38.99) < 1e-9 && Math.Abs(point.X + 76.94) < 1e-9);
    Assert.Equal(1, await s.Db.DeviceDiagramParts.CountAsync());
  }

  [Fact]
  public async Task APointAskedForAgainMovesTheDiagramAlreadyDrawn()
  {
    var s = await CreateAsync();
    await SeedCameraAsync(s);
    await SeedModelAsync(s);
    await SeedTypeAsync(s, new TypeSpec(), (LampToken, DeviceObjectId.RelayPart, DiagramPartMatch.Token, LampToken));

    await AttachAsync(s);
    var objectGuid = (await s.Db.DeviceDiagrams.SingleAsync()).object_id;

    // The same canvas, another click: the node already hangs where it should, and the point is
    // still the administrator's to choose. A run that returned early would drop the request.
    await AttachAsync(s, target: new DiagramTarget(
      Utils.ConvertGuidToObjectId(Space), Lat: 38.95, Lon: -76.90));

    var geo = await s.Db.GeoObjects.SingleAsync(g => g.id == objectGuid);
    Assert.True(geo.figure is Point point
                && Math.Abs(point.Y - 38.95) < 1e-9 && Math.Abs(point.X + 76.90) < 1e-9);
  }

  [Fact]
  public async Task OpeningADrawnDiagramFromTheCameraLeavesItOnItsCanvas()
  {
    var s = await CreateAsync();
    await SeedCameraAsync(s);
    await SeedModelAsync(s);
    await SeedTypeAsync(s, new TypeSpec(), (LampToken, DeviceObjectId.RelayPart, DiagramPartMatch.Token, LampToken));

    // The camera is drawn on two canvases; the diagram is on the second one, not the reference.
    await SeedPictureAsync(s);
    var elsewhere = await SeedSpaceAsync(s, SecondPlanName);
    var elsewherePicture = await SeedPictureAsync(s, elsewhere, false);

    await s.Diagrams.PlaceDeviceDiagramAsync(
      DeviceObjectId.CameraSource, CameraId, null, IDeviceDiagramService.DefaultSlot,
      new DiagramTarget(Utils.ConvertGuidToObjectId(elsewhere), Lat: 38.99, Lon: -76.94), default);
    var objectGuid = (await s.Db.DeviceDiagrams.SingleAsync()).object_id;
    Assert.Equal(elsewherePicture, (await s.Db.Markers.SingleAsync(m => m.id == objectGuid)).parent_id);

    // Opening it from the camera names no canvas. The figure is drawn on the second plan, so the
    // node stays there: moving it under the reference picture would leave the figure behind in
    // another plan's coordinates.
    await OpenWithTheCameraAsync(s);

    Assert.Equal(elsewherePicture, (await s.Db.Markers.SingleAsync(m => m.id == objectGuid)).parent_id);
    Assert.Equal(elsewhere, (await s.Db.GeoObjects.SingleAsync(g => g.id == objectGuid)).canvas_id);
  }

  [Fact]
  public async Task APointIsNotTakenFromAnotherCanvas()
  {
    var s = await CreateAsync();
    await SeedCameraAsync(s);
    await SeedModelAsync(s);
    await SeedTypeAsync(s, new TypeSpec(), (LampToken, DeviceObjectId.RelayPart, DiagramPartMatch.Token, LampToken));

    // Drawn on the tests' canvas at a point of its own.
    await AttachAsync(s);
    var objectGuid = (await s.Db.DeviceDiagrams.SingleAsync()).object_id;

    // Another canvas, no point named: the middle of its own view, not the coordinates the diagram
    // has on the first one — those are the first plan's metres.
    var elsewhere = await SeedSpaceAsync(s, SecondPlanName);
    var space = await s.Db.Spaces.SingleAsync(x => x.object_id == elsewhere);
    space.view_south = 38.9; space.view_north = 39.0;
    space.view_west = -77.0; space.view_east = -76.9;
    await s.Db.SaveChangesAsync();

    await AttachAsync(s, target: new DiagramTarget(Utils.ConvertGuidToObjectId(elsewhere)));

    var geo = await s.Db.GeoObjects.SingleAsync(g => g.id == objectGuid);
    Assert.Equal(elsewhere, geo.canvas_id);
    Assert.Equal(38.95, geo.figure!.Centroid.Y, 6);
    Assert.Equal(-76.95, geo.figure!.Centroid.X, 6);
  }

  [Fact]
  public async Task ATieBetweenTemplatesMakesAnEmptySheet()
  {
    var s = await CreateAsync();
    await SeedCameraAsync(s);
    await SeedModelAsync(s);
    await SeedPartAsync(s, DeviceObjectId.RelayPart, LampToken);
    await SeedTypeAsync(s, new TypeSpec(Name: RearPanelName),
      (LampToken, DeviceObjectId.RelayPart, DiagramPartMatch.Token, LampToken));
    await SeedTypeAsync(s, new TypeSpec(Name: FrontPanelName),
      (LampToken, DeviceObjectId.RelayPart, DiagramPartMatch.Token, LampToken));

    await AttachAsync(s);

    // Which panel to draw is the administrator's to say: a wrong one looks like a right one, so
    // neither is drawn and the sheet is blank.
    var row = await s.Db.DeviceDiagrams.SingleAsync();
    Assert.Null(row.diagram_type_id);
    Assert.Empty(s.Db.DeviceDiagramParts);
  }

  [Fact]
  public async Task ATemplateThatDoesNotAttachItselfWaitsForTheAdministrator()
  {
    var s = await CreateAsync();
    await SeedCameraAsync(s);
    await SeedModelAsync(s);
    await SeedPartAsync(s, DeviceObjectId.RelayPart, LampToken);
    var typeId = await SeedTypeAsync(s, new TypeSpec(AutoAttach: false),
      (LampToken, DeviceObjectId.RelayPart, DiagramPartMatch.Token, LampToken));

    await EnsureAsync(s);
    Assert.Empty(s.Db.DeviceDiagrams);

    await AttachAsync(s, Utils.ConvertGuidToObjectId(typeId));

    var row = await s.Db.DeviceDiagrams.SingleAsync();
    Assert.Equal(typeId, row.diagram_type_id);
    Assert.False(row.auto_created);
    Assert.Equal(1, await s.Db.DeviceDiagramParts.CountAsync());
  }

  [Fact]
  public async Task TwoCamerasOfTheSameModelGetTheirOwnDiagrams()
  {
    var s = await CreateAsync();
    await SeedCameraAsync(s);
    await SeedCameraAsync(s, OtherCameraId);
    await SeedModelAsync(s);
    await SeedModelAsync(s, OtherCameraId);
    await SeedPartAsync(s, DeviceObjectId.RelayPart, LampToken);
    await SeedPartAsync(s, DeviceObjectId.RelayPart, LampToken, cameraId: OtherCameraId);
    await SeedTypeAsync(s, new TypeSpec(), (LampToken, DeviceObjectId.RelayPart, DiagramPartMatch.Token, LampToken));

    await AttachAsync(s);
    await AttachAsync(s, cameraId: OtherCameraId);

    Assert.Equal(2, await s.Db.DeviceDiagrams.CountAsync());
    Assert.Equal(2, await s.Db.DeviceDiagramParts.CountAsync());
    Assert.Equal(
      2, (await s.Db.DeviceDiagramParts.ToListAsync())
         .Select(p => p.replica_object_id).Distinct().Count());
  }

  [Fact]
  public async Task TheRegionFollowsTheTemplate()
  {
    var s = await CreateAsync();
    await SeedCameraAsync(s);
    await SeedModelAsync(s);
    await SeedPartAsync(s, DeviceObjectId.RelayPart, LampToken);
    var typeId = await SeedTypeAsync(s, new TypeSpec(), (LampToken, DeviceObjectId.RelayPart, DiagramPartMatch.Token, LampToken));

    await AttachAsync(s);
    var replica = (await s.Db.DeviceDiagramParts.SingleAsync()).replica_object_id;

    // The administrator moves the binding to another region of the same type.
    var binding = await s.Db.DiagramTypeParts.SingleAsync();
    s.Db.DiagramTypeParts.Remove(binding);
    s.Db.DiagramTypeRegions.Add(new DBDiagramTypeRegion
    {
      diagram_type_id = typeId,
      region_key = LampRegionTwo,
      geometry = new DBDiagramCoord { width = 10, height = 10 },
    });
    await s.Db.SaveChangesAsync();
    s.Db.DiagramTypeParts.Add(new DBDiagramTypePart
    {
      diagram_type_id = typeId,
      region_key = LampRegionTwo,
      part_kind = DeviceObjectId.RelayPart,
      match_by = DiagramPartMatch.Token,
      match_value = LampToken,
    });
    await s.Db.SaveChangesAsync();

    var rules = await s.Db.DiagramTypeParts.ToListAsync();
    Assert.Equal(LampRegionTwo, Assert.Single(rules).region_key);

    await EnsureAsync(s);

    var rowNow = await s.Db.DeviceDiagrams.SingleAsync();
    Assert.Equal(typeId, rowNow.diagram_type_id);
    Assert.Equal(1, await s.Db.DeviceDiagramParts.CountAsync());

    var managed = await s.Db.DeviceDiagramParts.SingleAsync();
    Assert.Equal(LampRegionTwo, managed.region_key);
    Assert.Equal(LampRegionTwo, (await s.Db.Diagrams.SingleAsync(d => d.id == replica)).region_id);
  }

  [Fact]
  public async Task ChangingTheTypeKeepsThePlaceAndThePicture()
  {
    var s = await CreateAsync();
    await SeedCameraAsync(s);
    await SeedModelAsync(s);
    var first = await SeedTypeAsync(s, new TypeSpec());
    var second = await SeedTypeAsync(s, new TypeSpec(Name: FrontPanelName));

    // The space has a type of its own here: a region is a key of the parent's type, and this test
    // hangs the diagram directly under it.
    var spaceType = await SeedTypeAsync(
      s, new TypeSpec(Name: SecondPlanName),
      (LampRegionTwo, string.Empty, string.Empty, string.Empty));
    await new DiagramService(s.Db).UpdateListAsync([
      new DiagramDTO { id = Space.ToString(), dgr_type = spaceType.ToString() }
    ]);

    var diagramId = await AttachAsync(
      s, first.ToString(), target: new DiagramTarget(Space.ToString(), Lat: 1, Lon: 2));

    // A place and a picture of its own, put there by the editor as an ordinary diagram write.
    await new DiagramService(s.Db).UpdateListAsync([
      new DiagramDTO
      {
        id = diagramId,
        region_id = LampRegionTwo,
        geometry = new DiagramCoordDTO
        {
          left = 5,
          top = 6,
          width = PlacementWidth,
          height = PlacementHeight,
        },
        file_id = Utils.ConvertGuidToObjectId(Guid.NewGuid()),
      }
    ]);
    var picture = (await s.Db.Diagrams.SingleAsync(
      d => d.id == Utils.ConvertObjectIdToGuid(diagramId))).file_id;

    await OpenWithTheCameraAsync(s, second.ToString());

    // A template change is not a move: where it stands and its own picture stay.
    var row = await s.Db.Diagrams.SingleAsync(d => d.id == Utils.ConvertObjectIdToGuid(diagramId));
    Assert.Equal(Utils.ConvertObjectIdToGuid(second.ToString()), row.dgr_type);
    Assert.Equal(LampRegionTwo, row.region_id);
    Assert.Equal(PlacementWidth, row.geometry!.width);
    Assert.Equal(PlacementHeight, row.geometry.height);
    Assert.Equal(picture, row.file_id);
  }

  [Fact]
  public async Task RemovingTheCameraTakesTheRepresentationsOfItsDiagram()
  {
    var s = await CreateAsync();
    await SeedCameraAsync(s);
    await SeedModelAsync(s);
    var type = await SeedTypeAsync(s, new TypeSpec());
    var diagramId = await AttachAsync(
      s, type.ToString(), target: new DiagramTarget(Space.ToString()));

    // A representation on the same canvas: an ordinary replica whose place is its own.
    await s.Map.UpdateHierarchyAsync([
      new BaseMarkerDTO
      {
        parent_id = Utils.ConvertGuidToObjectId(Space),
        owner_id = diagramId,
        name = ShownPanelName,
      }
    ]);
    var shown = await s.Db.Markers.SingleAsync(
      m => m.owner_id == Utils.ConvertObjectIdToGuid(diagramId));
    await new DiagramService(s.Db).UpdateListAsync([
      new DiagramDTO
      {
        id = Utils.ConvertGuidToObjectId(shown.id),
        geometry = new DiagramCoordDTO { left = 1, top = 2, width = 3, height = 4 },
      }
    ]);

    await s.Nodes.RemoveCameraAsync(CameraId, default);

    // `owner_id` carries no foreign key: nothing but this takes the copy away.
    Assert.False(await s.Db.Markers.AnyAsync(m => m.id == shown.id));
    // The canvas it stood on is not the device's.
    Assert.True(await s.Db.Markers.AnyAsync(m => m.id == Space));
  }

  [Fact]
  public async Task RemovingTheCameraTakesTheDiagramWithIt()
  {
    var s = await CreateAsync();
    await SeedCameraAsync(s);
    await SeedModelAsync(s);
    await SeedPartAsync(s, DeviceObjectId.RelayPart, LampToken);
    await SeedTypeAsync(s, new TypeSpec(), (LampToken, DeviceObjectId.RelayPart, DiagramPartMatch.Token, LampToken));

    await AttachAsync(s);
    var row = await s.Db.DeviceDiagrams.SingleAsync();
    var replica = (await s.Db.DeviceDiagramParts.SingleAsync()).replica_object_id;

    await s.Nodes.RemoveCameraAsync(CameraId, default);

    // `parent_id` has no foreign key: nothing would have taken these but this.
    Assert.Empty(s.Db.DeviceDiagrams);
    Assert.Empty(s.Db.DeviceDiagramParts);
    Assert.False(await s.Db.Markers.AnyAsync(m => m.id == row.object_id));
    Assert.False(await s.Db.Markers.AnyAsync(m => m.id == replica));
  }

  // ── Binding a diagram the administrator drew ────────────────────────────────────────────────

  /// <summary>A plain node of the map: the tree has it, and nothing else does.</summary>
  private static async Task<string> Node(
    Services s, string name = "node", string? parent = null, string? owner = null)
  {
    var node = new BaseMarkerDTO
    {
      id = Guid.NewGuid().ToString(),
      parent_id = parent,
      owner_id = owner,
      name = name,
    };
    await s.Map.UpdateHierarchyAsync([node]);
    return node.id!;
  }

  /// <summary>
  /// A diagram of the administrator's own — a nested diagram on a canvas — which is nobody's device
  /// diagram until <see cref="BindAsync(Services, string, string, string?)"/> says so.
  /// </summary>
  private static async Task<string> SeedSpareDiagramAsync(
    Services s, string name, Guid? parent = null, string? region = null)
  {
    var id = Guid.NewGuid();
    await s.Map.UpdateHierarchyAsync([
      new BaseMarkerDTO
      {
        id = Utils.ConvertGuidToObjectId(id),
        parent_id = parent is { } at ? Utils.ConvertGuidToObjectId(at) : null,
        name = name,
      }
    ]);
    s.Db.Diagrams.Add(new DBDiagram { id = id, region_id = region });
    await s.Db.SaveChangesAsync();
    return Utils.ConvertGuidToObjectId(id);
  }

  private static Task<string?> BindAsync(
    Services s, string nodeId, string cameraId = CameraId, string? typeId = null) =>
    s.Diagrams.BindAsync(
      nodeId, DeviceObjectId.CameraSource, cameraId, typeId,
      IDeviceDiagramService.DefaultSlot, default);

  private static Task<bool> UnbindAsync(Services s, string nodeId) =>
    s.Diagrams.UnbindAsync(nodeId, default);

  [Fact]
  public async Task ABoundDiagramIsMaintainedLikeAnAutomaticOne()
  {
    var s = await CreateAsync();
    await SeedCameraAsync(s);
    await SeedModelAsync(s);
    await SeedPartAsync(s, DeviceObjectId.RelayPart, LampToken);
    var typeId = await SeedTypeAsync(
      s, new TypeSpec(), (LampToken, DeviceObjectId.RelayPart, DiagramPartMatch.Token, LampToken));
    var canvas = await SeedMnemoschemeAsync(s, CanvasName, LeftRegion, RightRegion);
    var spare = await SeedSpareDiagramAsync(s, SpareName, canvas.Node, LeftRegion);

    var bound = await BindAsync(s, spare);

    Assert.Equal(spare, bound);
    var row = await s.Db.DeviceDiagrams.SingleAsync();
    Assert.Equal(Guid.Parse(spare), row.object_id);
    // The template is the device's: the diagram itself had none.
    Assert.Equal(typeId, row.diagram_type_id);
    // And the part is drawn inside it, in the region the template gives that part.
    var managed = await s.Db.DeviceDiagramParts.SingleAsync();
    Assert.Equal(LampToken, managed.region_key);
    var replica = await s.Db.Markers.SingleAsync(m => m.id == managed.replica_object_id);
    Assert.Equal(Guid.Parse(spare), replica.parent_id);
    // Binding is not placing: the diagram stays where the administrator drew it.
    Assert.Equal(canvas.Node, (await s.Db.Markers.SingleAsync(m => m.id == Guid.Parse(spare))).parent_id);
    Assert.Equal(LeftRegion, (await s.Db.Diagrams.SingleAsync(d => d.id == Guid.Parse(spare))).region_id);
  }

  [Fact]
  public async Task BindingTheSameDiagramAgainChangesNothing()
  {
    var s = await CreateAsync();
    await SeedCameraAsync(s);
    await SeedModelAsync(s);
    await SeedPartAsync(s, DeviceObjectId.RelayPart, LampToken);
    await SeedTypeAsync(
      s, new TypeSpec(), (LampToken, DeviceObjectId.RelayPart, DiagramPartMatch.Token, LampToken));
    var spare = await SeedSpareDiagramAsync(s, SpareName);

    await BindAsync(s, spare);
    var rows = await s.Db.DeviceDiagrams.ToListAsync();
    var managed = await s.Db.DeviceDiagramParts.ToListAsync();

    // A repeat after a network timeout: the same answer, and not a second diagram or a second copy
    // of the part.
    Assert.Equal(spare, await BindAsync(s, spare));
    Assert.Equal(
      rows.Select(r => r.object_id), (await s.Db.DeviceDiagrams.ToListAsync()).Select(r => r.object_id));
    Assert.Equal(
      managed.Select(m => m.replica_object_id),
      (await s.Db.DeviceDiagramParts.ToListAsync()).Select(m => m.replica_object_id));
  }

  [Fact]
  public async Task ADiagramBoundToAnotherDeviceMustBeUnboundFirst()
  {
    var s = await CreateAsync();
    await SeedCameraAsync(s);
    var spare = await SeedSpareDiagramAsync(s, SpareName);
    await BindAsync(s, spare);

    var error = await Assert.ThrowsAsync<DeviceDiagramException>(
      () => BindAsync(s, spare, OtherCameraId));

    Assert.Contains("another device", error.Message);
    Assert.Equal(CameraId, (await s.Db.DeviceDiagrams.SingleAsync()).source_id);
  }

  [Fact]
  public async Task ADeviceThatAlreadyHasADiagramForThisViewIsRefused()
  {
    var s = await CreateAsync();
    await SeedCameraAsync(s);
    await BindAsync(s, await SeedSpareDiagramAsync(s, SpareName));
    var second = await SeedSpareDiagramAsync(s, "second panel");

    var error = await Assert.ThrowsAsync<DeviceDiagramException>(
      () => BindAsync(s, second));

    Assert.Contains("already has a diagram", error.Message);
    Assert.Equal(1, await s.Db.DeviceDiagrams.CountAsync());
  }

  [Fact]
  public async Task BindingToACameraVmsCfgDoesNotKnowIsRefused()
  {
    var s = await CreateAsync();
    // The camera is gone from the configuration; its removal event has not been handled yet.
    s.Catalog.Known = _ => false;
    var spare = await SeedSpareDiagramAsync(s, SpareName);

    var error = await Assert.ThrowsAsync<DeviceDiagramException>(() => BindAsync(s, spare));

    Assert.Contains("does not exist", error.Message);
    Assert.Empty(await s.Db.DeviceDiagrams.ToListAsync());
  }

  [Fact]
  public async Task ANodeThatIsNotADiagramCannotBeBound()
  {
    var s = await CreateAsync();
    await SeedCameraAsync(s);
    var plain = await Node(s);

    var error = await Assert.ThrowsAsync<DeviceDiagramException>(() => BindAsync(s, plain));

    Assert.Contains("not a diagram", error.Message);
  }

  [Fact]
  public async Task AReplicaCannotBeBound()
  {
    var s = await CreateAsync();
    await SeedCameraAsync(s);
    var owner = await Node(s);
    var canvas = await Node(s);
    var copy = await Node(s, parent: canvas, owner: owner);
    await new DiagramService(s.Db).UpdateListAsync([new DiagramDTO { id = copy }]);

    var error = await Assert.ThrowsAsync<DeviceDiagramException>(() => BindAsync(s, copy));

    Assert.Contains("replica", error.Message);
  }

  [Fact]
  public async Task ARegionThatStandsForAPartCannotHoldADeviceDiagram()
  {
    var s = await CreateAsync();
    await SeedCameraAsync(s);
    // The parent's template draws a device part in this region; a diagram cannot take that slot.
    var parentType = await SeedTypeAsync(
      s, new TypeSpec(Name: CanvasName),
      (LampToken, DeviceObjectId.RelayPart, DiagramPartMatch.Token, LampToken));
    var canvas = Guid.NewGuid();
    await s.Map.UpdateHierarchyAsync([
      new BaseMarkerDTO { id = Utils.ConvertGuidToObjectId(canvas), name = CanvasName }
    ]);
    s.Db.Diagrams.Add(new DBDiagram { id = canvas, dgr_type = parentType });
    await s.Db.SaveChangesAsync();
    var spare = await SeedSpareDiagramAsync(s, SpareName, canvas, LampToken);

    var error = await Assert.ThrowsAsync<DeviceDiagramException>(() => BindAsync(s, spare));

    Assert.Contains("stands for a part", error.Message);
    Assert.Empty(await s.Db.DeviceDiagrams.ToListAsync());
  }

  [Fact]
  public async Task UnbindingLeavesTheDiagramItsTypeItsPlaceAndItsManualChildren()
  {
    var s = await CreateAsync();
    await SeedCameraAsync(s);
    await SeedModelAsync(s);
    await SeedPartAsync(s, DeviceObjectId.RelayPart, LampToken);
    await SeedTypeAsync(
      s, new TypeSpec(), (LampToken, DeviceObjectId.RelayPart, DiagramPartMatch.Token, LampToken));
    var canvas = await SeedMnemoschemeAsync(s, CanvasName, LeftRegion);
    var spare = await SeedSpareDiagramAsync(s, SpareName, canvas.Node, LeftRegion);
    await BindAsync(s, spare);

    // Something the administrator drew in it, and something a template change will not touch.
    var manual = await Node(s, parent: spare);
    await new DiagramService(s.Db).UpdateListAsync([new DiagramDTO
    {
      id = manual,
      geometry = new DiagramCoordDTO { left = 1, top = 2, width = 3, height = 4 },
    }]);
    var managed = await s.Db.DeviceDiagramParts.SingleAsync();
    var replica = managed.replica_object_id;

    Assert.True(await UnbindAsync(s, spare));

    Assert.Empty(await s.Db.DeviceDiagrams.ToListAsync());
    Assert.Empty(await s.Db.DeviceDiagramParts.ToListAsync());
    // The replicas the map made go with the binding.
    Assert.False(await s.Db.Markers.AnyAsync(m => m.id == replica));
    // The diagram, its type, its place and the drawing stay.
    var node = await s.Db.Markers.SingleAsync(m => m.id == Guid.Parse(spare));
    Assert.Equal(canvas.Node, node.parent_id);
    var row = await s.Db.Diagrams.SingleAsync(d => d.id == Guid.Parse(spare));
    Assert.NotNull(row.dgr_type);
    Assert.Equal(LeftRegion, row.region_id);
    Assert.True(await s.Db.Markers.AnyAsync(m => m.id == Guid.Parse(manual)));
    Assert.True(await s.Db.Diagrams.AnyAsync(d => d.id == Guid.Parse(manual)));
  }

  [Fact]
  public async Task UnbindingTwiceChangesNothing()
  {
    var s = await CreateAsync();
    await SeedCameraAsync(s);
    var spare = await SeedSpareDiagramAsync(s, SpareName);
    await BindAsync(s, spare);

    Assert.True(await UnbindAsync(s, spare));
    Assert.False(await UnbindAsync(s, spare));
    Assert.True(await s.Db.Markers.AnyAsync(m => m.id == Guid.Parse(spare)));
  }

  [Fact]
  public async Task TheCameraTakesABoundDiagramWithIt()
  {
    var s = await CreateAsync();
    await SeedCameraAsync(s);
    var spare = await SeedSpareDiagramAsync(s, SpareName);
    await BindAsync(s, spare);

    await s.Nodes.RemoveCameraAsync(CameraId, default);

    // A diagram bound by hand belongs to the camera exactly as an automatic one does.
    Assert.False(await s.Db.Markers.AnyAsync(m => m.id == Guid.Parse(spare)));
    Assert.Empty(await s.Db.DeviceDiagrams.ToListAsync());
  }

  [Fact]
  public async Task ARepresentationOfAnUnboundDiagramStaysAndKeepsShowingIt()
  {
    var s = await PlacedCameraAsync();
    await SeedPictureAsync(s);
    var first = await SeedMnemoschemeAsync(s, CanvasName, LeftRegion, RightRegion);
    var spare = await SeedSpareDiagramAsync(s, SpareName, first.Node, LeftRegion);
    await BindAsync(s, spare);

    var second = await SeedMnemoschemeAsync(s, AnotherCanvasName, LeftRegion);
    var shown = await AttachAsync(
      s, target: new DiagramTarget(second.Node.ToString(), RegionId: LeftRegion));
    Assert.NotNull(shown);
    Assert.Equal(
      Guid.Parse(spare),
      (await s.Db.Markers.SingleAsync(m => m.id == Guid.Parse(shown!))).owner_id);

    Assert.True(await UnbindAsync(s, spare));

    // Unbinding is not deleting: the copy stands where it was, and now shows an ordinary diagram —
    // the same node, with the same type and the same children.
    var copy = await s.Db.Markers.SingleAsync(m => m.id == Guid.Parse(shown!));
    Assert.Equal(Guid.Parse(spare), copy.owner_id);
    Assert.Equal(second.Node, copy.parent_id);
    Assert.NotNull((await s.Db.Diagrams.SingleAsync(d => d.id == Guid.Parse(spare))).dgr_type);
  }

  [Fact]
  public async Task ADiagramSurvivesTheCameraWhenItHasBeenUnbound()
  {
    var s = await CreateAsync();
    await SeedCameraAsync(s);
    var spare = await SeedSpareDiagramAsync(s, SpareName);
    await BindAsync(s, spare);
    await UnbindAsync(s, spare);

    await s.Nodes.RemoveCameraAsync(CameraId, default);

    Assert.True(await s.Db.Markers.AnyAsync(m => m.id == Guid.Parse(spare)));
  }

  [Fact]
  public async Task ATemplateForAnotherModelIsRefused()
  {
    var s = await CreateAsync();
    await SeedCameraAsync(s);
    await SeedModelAsync(s);
    var other = await SeedTypeAsync(s, new TypeSpec(Name: FrontPanelName, Model: PtzModel));
    var spare = await SeedSpareDiagramAsync(s, SpareName);

    var error = await Assert.ThrowsAsync<DeviceDiagramException>(
      () => BindAsync(s, spare, typeId: other.ToString()));

    Assert.Contains("does not describe this device", error.Message);
    Assert.Empty(await s.Db.DeviceDiagrams.ToListAsync());
  }

  [Fact]
  public async Task ADeviceOfAnotherKindDoesNotTakeTheTemplate()
  {
    var s = await CreateAsync();
    await SeedCameraAsync(s);
    await SeedModelAsync(s);
    var typeId = await SeedTypeAsync(s, new TypeSpec());
    var type = await s.Db.DiagramTypes.SingleAsync(t => t.id == typeId);
    type.source_kind = SensorKind;
    await s.Db.SaveChangesAsync();
    var spare = await SeedSpareDiagramAsync(s, SpareName);

    var error = await Assert.ThrowsAsync<DeviceDiagramException>(
      () => BindAsync(s, spare, typeId: typeId.ToString()));

    Assert.Contains("another kind of device", error.Message);
  }

  [Fact]
  public async Task ANamedTemplateIsRefusedWhileTheDeviceModelIsUnknown()
  {
    var s = await CreateAsync();
    await SeedCameraAsync(s);
    // No device info: nothing can vouch for a template, and the request said one outright.
    var typeId = await SeedTypeAsync(s, new TypeSpec());
    var spare = await SeedSpareDiagramAsync(s, SpareName);

    var error = await Assert.ThrowsAsync<DeviceDiagramException>(
      () => BindAsync(s, spare, typeId: typeId.ToString()));

    Assert.Contains("not known", error.Message);
  }

  [Fact]
  public async Task BindingWithoutATemplateWaitsForTheModel()
  {
    var s = await CreateAsync();
    await SeedCameraAsync(s);
    await SeedPartAsync(s, DeviceObjectId.RelayPart, LampToken);
    var typeId = await SeedTypeAsync(
      s, new TypeSpec(), (LampToken, DeviceObjectId.RelayPart, DiagramPartMatch.Token, LampToken));
    var spare = await SeedSpareDiagramAsync(s, SpareName);

    // The model is not known yet: the diagram is bound as it is, with no type of its own.
    await BindAsync(s, spare);
    Assert.Null((await s.Db.DeviceDiagrams.SingleAsync()).diagram_type_id);

    await SeedModelAsync(s);
    await EnsureAsync(s);

    // Once the model arrives, reconciliation takes the template that describes it.
    Assert.Equal(typeId, (await s.Db.DeviceDiagrams.SingleAsync()).diagram_type_id);
    Assert.Single(await s.Db.DeviceDiagramParts.ToListAsync());
  }

  [Fact]
  public async Task ChangingTheCamerasPartsReachesEveryRepresentation()
  {
    var s = await PlacedCameraAsync();
    await SeedPictureAsync(s);
    var first = await SeedMnemoschemeAsync(s, CanvasName, LeftRegion);
    var second = await SeedMnemoschemeAsync(s, AnotherCanvasName, LeftRegion);

    var one = await AttachAsync(
      s, target: new DiagramTarget(first.Node.ToString(), RegionId: LeftRegion));
    var two = await AttachAsync(
      s, target: new DiagramTarget(second.Node.ToString(), RegionId: LeftRegion));

    Assert.NotNull(one);
    Assert.NotNull(two);
    var canonical = await s.Db.DeviceDiagrams.SingleAsync();
    Assert.Single(await s.Db.DeviceDiagramParts.ToListAsync());

    // A second part is configured and the template learns where to draw it: one reconciliation, and
    // both copies show it, because they show the same diagram.
    var type = await s.Db.DiagramTypes.SingleAsync(t => t.id == canonical.diagram_type_id);
    type.regions.Add(new DBDiagramTypeRegion
    {
      diagram_type_id = type.id,
      region_key = InputToken,
      geometry = new DBDiagramCoord { width = 10, height = 10 },
    });
    type.parts.Add(new DBDiagramTypePart
    {
      diagram_type_id = type.id,
      region_key = InputToken,
      part_kind = DeviceObjectId.DigitalInputPart,
      match_by = DiagramPartMatch.Token,
      match_value = InputToken,
    });
    await s.Db.SaveChangesAsync();
    await SeedPartAsync(s, DeviceObjectId.DigitalInputPart, InputToken);

    await EnsureAsync(s);

    Assert.Equal(2, await s.Db.DeviceDiagramParts.CountAsync());
    // The copies never had children of their own: they stand on the canvas and show the diagram.
    foreach (var shown in new[] { one!, two! })
      Assert.Empty(await s.Db.Markers.Where(m => m.parent_id == Guid.Parse(shown)).ToListAsync());

    // Deleting one copy leaves the other, and the diagram both show.
    await s.Map.RemoveAsync([two!]);
    Assert.False(await s.Db.Markers.AnyAsync(m => m.id == Guid.Parse(two)));
    Assert.True(await s.Db.Markers.AnyAsync(m => m.id == Guid.Parse(one)));
    Assert.True(await s.Db.Markers.AnyAsync(m => m.id == canonical.object_id));
  }
}
