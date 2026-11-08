using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
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

    public void Invalidate(string cameraId) { }
  }

  private sealed class FakeCatalog : ICameraCatalog
  {
    public Task<CameraInfo?> GetAsync(string cameraId, CancellationToken ct) =>
      Task.FromResult<CameraInfo?>(new CameraInfo(cameraId, "Camera " + cameraId, "srv"));

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
    var updates = new MapUpdateService(map, tracks, statesUpdate);
    var geo = new GeoService(db, levels);

    var settings = new FakeSettings();
    var catalog = new FakeCatalog();
    var nodes = new DeviceNodeService(
      db, map, updates, catalog, settings, NullLogger<DeviceNodeService>.Instance);
    var diagrams = new DeviceDiagramService(
      db, map, updates, new DiagramService(db), settings, catalog, geo,
      NullLogger<DeviceDiagramService>.Instance);

    // The canvas the diagrams stand on: a diagram is a map object, and one of these tests needs
    // only the space to exist.
    db.Markers.Add(new DBMarker { id = Space, name = "Floor" });
    db.Spaces.Add(new DBSpace { object_id = Space, kind = SpaceKinds.Map });
    await db.SaveChangesAsync();

    return new Services(db, map, diagrams, nodes, settings, catalog);
  }

  private static Task SeedCameraAsync(Services s, string cameraId = CameraId) =>
    s.Map.UpdateHierarchyAsync([
      new BaseMarkerDTO { id = CameraObjectId.For(cameraId), name = "Camera " + cameraId }
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
    s.Diagrams.EnsureAsync(
      DeviceObjectId.CameraSource, cameraId, IDeviceDiagramService.DefaultSlot, default);

  /// Draws the device on the tests' canvas; without a type the one its model names is taken.
  private static Task<string?> AttachAsync(
    Services s, string? typeId = null, string cameraId = CameraId, DiagramSite? site = null) =>
    s.Diagrams.AttachAsync(
      DeviceObjectId.CameraSource, cameraId, typeId, IDeviceDiagramService.DefaultSlot,
      site ?? new DiagramSite(Utils.ConvertGuidToObjectId(Space), 38.99, -76.94), default);

  [Fact]
  public async Task ATemplateDrawsTheConfiguredPartsOnItsCanvas()
  {
    var s = await CreateAsync();
    await SeedCameraAsync(s);
    await SeedModelAsync(s);
    await SeedPartAsync(s, DeviceObjectId.DigitalInputPart, "input");
    await SeedPartAsync(s, DeviceObjectId.RelayPart, "lamp");
    var typeId = await SeedTypeAsync(s, new TypeSpec(),
      ("input", DeviceObjectId.DigitalInputPart, DiagramPartMatch.Token, "input"),
      ("lamp", DeviceObjectId.RelayPart, DiagramPartMatch.Token, "lamp"),
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
    Assert.Contains(managed, m => m.region_key == "input");
    Assert.Contains(managed, m => m.region_key == "lamp");

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
    await SeedPartAsync(s, DeviceObjectId.RelayPart, "lamp");
    await SeedTypeAsync(s, new TypeSpec(), ("lamp", DeviceObjectId.RelayPart, DiagramPartMatch.Token, "lamp"));

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
      DeviceObjectId.CameraSource, CameraId, DeviceObjectId.RelayPart, "lamp"));
    s.Db.DeviceNodes.Add(new DBDeviceNode
    {
      object_id = relay,
      source_kind = DeviceObjectId.CameraSource,
      source_id = CameraId,
      part_kind = DeviceObjectId.RelayPart,
      part_token = "lamp",
    });
    await s.Db.SaveChangesAsync();
    await SeedTypeAsync(s, new TypeSpec(), ("lamp", DeviceObjectId.RelayPart, DiagramPartMatch.Token, "lamp"));

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
    await SeedPartAsync(s, DeviceObjectId.RelayPart, "lamp");

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
    await SeedTypeAsync(s, new TypeSpec(), ("lamp", DeviceObjectId.RelayPart, DiagramPartMatch.Token, "lamp"));

    // The camera is not drawn on this canvas, so there is no placement to stand beside.
    var space = await s.Db.Spaces.SingleAsync();
    space.view_south = 38.9; space.view_north = 39.0;
    space.view_west = -77.0; space.view_east = -76.9;
    await s.Db.SaveChangesAsync();

    await AttachAsync(s, site: new DiagramSite(Utils.ConvertGuidToObjectId(Space)));

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
    await SeedPartAsync(s, DeviceObjectId.RelayPart, "lamp");
    await SeedTypeAsync(s, new TypeSpec(), ("lamp", DeviceObjectId.RelayPart, DiagramPartMatch.Token, "lamp"));

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
    var lamp = await SeedPartAsync(s, DeviceObjectId.RelayPart, "lamp");
    await SeedTypeAsync(s, new TypeSpec(), ("lamp", DeviceObjectId.RelayPart, DiagramPartMatch.Token, "lamp"));

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
        name = "Lamp (by hand)",
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
    await SeedModelAsync(s, model: "Simulated fixed camera");
    await SeedPartAsync(s, DeviceObjectId.RelayPart, "lamp");
    await SeedTypeAsync(s, new TypeSpec(Name: "Rear panel"),
      ("lamp", DeviceObjectId.RelayPart, DiagramPartMatch.Token, "lamp"));
    await SeedTypeAsync(s, new TypeSpec(Name: "Front panel", Model: "Simulated PTZ camera"),
      ("lamp", DeviceObjectId.RelayPart, DiagramPartMatch.Token, "lamp"));

    await AttachAsync(s);
    var node = (await s.Db.DeviceDiagrams.SingleAsync()).object_id;
    var rear = await s.Db.DiagramTypes.SingleAsync(t => t.name == "Rear panel");

    // The device says it is another model now.
    var info = await s.Db.DeviceInfos.SingleAsync();
    info.model = "Simulated PTZ camera";
    await s.Db.SaveChangesAsync();

    await EnsureAsync(s);

    var row = await s.Db.DeviceDiagrams.SingleAsync();
    var front = await s.Db.DiagramTypes.SingleAsync(t => t.name == "Front panel");

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
    await SeedPartAsync(s, DeviceObjectId.RelayPart, "lamp");
    var typeId = await SeedTypeAsync(s, new TypeSpec(), ("lamp", DeviceObjectId.RelayPart, DiagramPartMatch.Token, "lamp"));

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
    await SeedPartAsync(s, DeviceObjectId.RelayPart, "lamp");
    await SeedTypeAsync(s, new TypeSpec(),
      ("lamp", DeviceObjectId.RelayPart, DiagramPartMatch.Token, "lamp"));

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
      ("lamp", DeviceObjectId.RelayPart, DiagramPartMatch.Token, "lamp"));
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
    await SeedPartAsync(s, DeviceObjectId.RelayPart, "lamp");

    await AttachAsync(s);
    Assert.Null((await s.Db.DeviceDiagrams.SingleAsync()).diagram_type_id);

    // The template is added later; the same button fills the sheet the administrator placed.
    var typeId = await SeedTypeAsync(s, new TypeSpec(),
      ("lamp", DeviceObjectId.RelayPart, DiagramPartMatch.Token, "lamp"));
    await AttachAsync(s);

    var row = await s.Db.DeviceDiagrams.SingleAsync();
    Assert.Equal(typeId, row.diagram_type_id);
    Assert.Equal(1, await s.Db.DeviceDiagramParts.CountAsync());
  }

  [Fact]
  public async Task APlacementMovesADiagramMadeUnderTheCameraOntoTheCanvas()
  {
    var s = await CreateAsync();
    await SeedCameraAsync(s);
    await SeedModelAsync(s);
    await SeedPartAsync(s, DeviceObjectId.RelayPart, "lamp");
    await SeedTypeAsync(s, new TypeSpec(), ("lamp", DeviceObjectId.RelayPart, DiagramPartMatch.Token, "lamp"));

    // A diagram made when they still hung under the camera: the node's parent is the camera.
    var objectId = DeviceObjectId.ForDeviceDiagram(
      DeviceObjectId.CameraSource, CameraId, IDeviceDiagramService.DefaultSlot);
    var objectGuid = Guid.Parse(objectId);
    await s.Map.UpdateHierarchyAsync([
      new BaseMarkerDTO { id = objectId, parent_id = CameraObjectId.For(CameraId), name = "Old panel" }
    ]);
    s.Db.DeviceDiagrams.Add(new DBDeviceDiagram
    {
      object_id = objectGuid,
      source_kind = DeviceObjectId.CameraSource,
      source_id = CameraId,
      slot = IDeviceDiagramService.DefaultSlot,
      auto_created = true,
    });
    await s.Db.SaveChangesAsync();

    await AttachAsync(s);

    // The button draws it here: under the canvas, with a point of its own, and its parts bound.
    var row = await s.Db.DeviceDiagrams.SingleAsync();
    Assert.Equal(objectGuid, row.object_id);
    Assert.Equal(Space, (await s.Db.Markers.SingleAsync(m => m.id == objectGuid)).parent_id);
    Assert.Equal(1, await s.Db.GeoObjects.CountAsync(g => g.id == objectGuid));
    Assert.Equal(1, await s.Db.DeviceDiagramParts.CountAsync());
  }

  [Fact]
  public async Task ATieBetweenTemplatesMakesAnEmptySheet()
  {
    var s = await CreateAsync();
    await SeedCameraAsync(s);
    await SeedModelAsync(s);
    await SeedPartAsync(s, DeviceObjectId.RelayPart, "lamp");
    await SeedTypeAsync(s, new TypeSpec(Name: "Rear panel"),
      ("lamp", DeviceObjectId.RelayPart, DiagramPartMatch.Token, "lamp"));
    await SeedTypeAsync(s, new TypeSpec(Name: "Front panel"),
      ("lamp", DeviceObjectId.RelayPart, DiagramPartMatch.Token, "lamp"));

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
    await SeedPartAsync(s, DeviceObjectId.RelayPart, "lamp");
    var typeId = await SeedTypeAsync(s, new TypeSpec(AutoAttach: false),
      ("lamp", DeviceObjectId.RelayPart, DiagramPartMatch.Token, "lamp"));

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
    await SeedPartAsync(s, DeviceObjectId.RelayPart, "lamp");
    await SeedPartAsync(s, DeviceObjectId.RelayPart, "lamp", cameraId: OtherCameraId);
    await SeedTypeAsync(s, new TypeSpec(), ("lamp", DeviceObjectId.RelayPart, DiagramPartMatch.Token, "lamp"));

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
    await SeedPartAsync(s, DeviceObjectId.RelayPart, "lamp");
    var typeId = await SeedTypeAsync(s, new TypeSpec(), ("lamp", DeviceObjectId.RelayPart, DiagramPartMatch.Token, "lamp"));

    await AttachAsync(s);
    var replica = (await s.Db.DeviceDiagramParts.SingleAsync()).replica_object_id;

    // The administrator moves the binding to another region of the same type.
    var binding = await s.Db.DiagramTypeParts.SingleAsync();
    s.Db.DiagramTypeParts.Remove(binding);
    s.Db.DiagramTypeRegions.Add(new DBDiagramTypeRegion
    {
      diagram_type_id = typeId,
      region_key = "lamp-2",
      geometry = new DBDiagramCoord { width = 10, height = 10 },
    });
    await s.Db.SaveChangesAsync();
    s.Db.DiagramTypeParts.Add(new DBDiagramTypePart
    {
      diagram_type_id = typeId,
      region_key = "lamp-2",
      part_kind = DeviceObjectId.RelayPart,
      match_by = DiagramPartMatch.Token,
      match_value = "lamp",
    });
    await s.Db.SaveChangesAsync();

    var rules = await s.Db.DiagramTypeParts.ToListAsync();
    Assert.Single(rules);
    Assert.Equal("lamp-2", rules[0].region_key);

    await EnsureAsync(s);

    var rowNow = await s.Db.DeviceDiagrams.SingleAsync();
    Assert.Equal(typeId, rowNow.diagram_type_id);
    Assert.Equal(1, await s.Db.DeviceDiagramParts.CountAsync());

    var managed = await s.Db.DeviceDiagramParts.SingleAsync();
    Assert.Equal("lamp-2", managed.region_key);
    Assert.Equal("lamp-2", (await s.Db.Diagrams.SingleAsync(d => d.id == replica)).region_id);
  }

  [Fact]
  public async Task RemovingTheCameraTakesTheDiagramWithIt()
  {
    var s = await CreateAsync();
    await SeedCameraAsync(s);
    await SeedModelAsync(s);
    await SeedPartAsync(s, DeviceObjectId.RelayPart, "lamp");
    await SeedTypeAsync(s, new TypeSpec(), ("lamp", DeviceObjectId.RelayPart, DiagramPartMatch.Token, "lamp"));

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
}
