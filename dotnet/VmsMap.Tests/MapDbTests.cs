using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using VmsMap.Db;
using VmsMap.Domain;

namespace VmsMap.Tests;

public class MapDbTests
{
  // The fixtures these tests share.
  private const string CameraLabel = "camera";
  private const string BuildingLabel = "building";
  private const string MainMapName = "main map";
  private const string FloorPlanName = "floor";
  private const string FloorRename = "floor 2";
  private const string PngMime = "image/png";
  private const string OwnerLabel = "owner";
  private const string ViewLabel = "view";
  private const string PlainLabel = "plain";
  private const string RackLabel = "rack";
  private const string RenamedLabel = "renamed";
  private const string VisualColor = "#ff0000";

  private static (VmsMapDbContext Db, MapDb Map) CreateDb()
  {
    var options = new DbContextOptionsBuilder<VmsMapDbContext>()
      .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
      .Options;

    var db = new VmsMapDbContext(options);
    return (db, new MapDb(db));
  }

  [Fact]
  public async Task UpdateHierarchyCopiesNewValuesOntoAnExistingNode()
  {
    var (db, map) = CreateDb();

    var parent = new BaseMarkerDTO { name = BuildingLabel };
    var child = new BaseMarkerDTO { name = "floor 1", parent_id = null };
    await map.UpdateHierarchyAsync(new[] { parent, child });

    var otherParent = new BaseMarkerDTO { name = "building 2" };
    await map.UpdateHierarchyAsync(new[] { otherParent });

    // Square's UpdateHierarchyAsync called Update() on the unchanged entity: this rename and
    // move were silently dropped.
    child.name = FloorRename;
    child.parent_id = otherParent.id;
    await map.UpdateHierarchyAsync(new[] { child });

    var stored = await db.Markers.SingleAsync(m => m.name == FloorRename);
    Assert.Equal(Utils.ConvertObjectIdToGuid(otherParent.id), stored.parent_id);
  }

  [Fact]
  public async Task ACopyOfADiagramCannotHoldNodes()
  {
    var (db, map) = CreateDb();

    var diagram = new BaseMarkerDTO { name = "panel" };
    var board = new BaseMarkerDTO { name = "board" };
    await map.UpdateHierarchyAsync([diagram, board]);
    db.Diagrams.Add(new DBDiagram { id = Guid.Parse(diagram.id!) });
    await db.SaveChangesAsync();

    // The diagram's copy: a replica standing on the board, opening on the diagram's content.
    var copy = new BaseMarkerDTO
    {
      name = "panel rep1",
      parent_id = board.id,
      owner_id = diagram.id,
    };
    await map.UpdateHierarchyAsync([copy]);

    // A reader answers a copy's children from the diagram it represents, so a node hung under the
    // copy itself would never be shown. The write is refused rather than silently lost.
    var hidden = new BaseMarkerDTO { name = "hidden", parent_id = copy.id };
    await Assert.ThrowsAsync<ArgumentException>(() => map.UpdateHierarchyAsync([hidden]));

    // The diagram itself takes children: that is where a copy's content comes from.
    var drawn = new BaseMarkerDTO { name = "drawn", parent_id = diagram.id };
    await map.UpdateHierarchyAsync([drawn]);
    Assert.NotNull(await db.Markers.FirstOrDefaultAsync(m => m.name == "drawn"));
  }

  [Fact]
  public async Task MovingANodeUnderItsOwnDescendantIsRefused()
  {
    var (db, map) = CreateDb();
    var container = new BaseMarkerDTO { name = "container" };
    await map.UpdateHierarchyAsync([container]);
    var child = new BaseMarkerDTO { name = "child", parent_id = container.id };
    await map.UpdateHierarchyAsync([child]);
    var grandchild = new BaseMarkerDTO { name = "grandchild", parent_id = child.id };
    await map.UpdateHierarchyAsync([grandchild]);

    container.parent_id = grandchild.id;
    await Assert.ThrowsAsync<ArgumentException>(() => map.UpdateHierarchyAsync([container]));

    // Nothing was written: the tree still has the container at the root.
    Assert.Null((await db.Markers.AsNoTracking().SingleAsync(m => m.id == Guid.Parse(container.id!))).parent_id);
    Assert.Equal(Guid.Parse(container.id!), (await db.Markers.AsNoTracking().SingleAsync(m => m.id == Guid.Parse(child.id!))).parent_id);
  }

  [Fact]
  public async Task AReplicaOfAReplicaIsWrittenAsAPictureOfTheOriginal()
  {
    var (db, map) = CreateDb();

    var original = new BaseMarkerDTO { name = CameraLabel };
    await map.UpdateHierarchyAsync([original]);
    var picture = new BaseMarkerDTO { name = CameraLabel, owner_id = original.id };
    await map.UpdateHierarchyAsync([picture]);

    // The second picture is dropped from the first picture's canvas: it must show the node both
    // show. Every read that answers through an owner resolves it in one hop, so a chain would make
    // its end answer nothing.
    var second = new BaseMarkerDTO { name = CameraLabel, owner_id = picture.id };
    await map.UpdateHierarchyAsync([second]);

    var stored = await db.Markers.AsNoTracking().SingleAsync(m => m.id == Guid.Parse(second.id!));
    Assert.Equal(Guid.Parse(original.id!), stored.owner_id);
    Assert.Equal(original.id, second.owner_id);
  }

  [Fact]
  public async Task ANodeCannotBeItsOwnOwner()
  {
    var (db, map) = CreateDb();

    var node = new BaseMarkerDTO { name = CameraLabel };
    await map.UpdateHierarchyAsync([node]);

    node.owner_id = node.id;
    await Assert.ThrowsAsync<ArgumentException>(() => map.UpdateHierarchyAsync([node]));

    Assert.Null((await db.Markers.AsNoTracking().SingleAsync(m => m.id == Guid.Parse(node.id!))).owner_id);
  }

  [Fact]
  public async Task DeletingANodeRemovesItsReplicasAndNothingElse()
  {
    var (db, map) = CreateDb();

    var owner = new BaseMarkerDTO { name = RackLabel };
    await map.UpdateHierarchyAsync([owner]);
    var replica = new BaseMarkerDTO { name = RackLabel, owner_id = owner.id };
    await map.UpdateHierarchyAsync([replica]);
    var other = new BaseMarkerDTO { name = "other" };
    await map.UpdateHierarchyAsync([other]);

    await map.RemoveAsync([owner.id!]);

    Assert.Null(await db.Markers.AsNoTracking().FirstOrDefaultAsync(m => m.id == Guid.Parse(owner.id!)));
    Assert.Null(await db.Markers.AsNoTracking().FirstOrDefaultAsync(m => m.id == Guid.Parse(replica.id!)));
    Assert.NotNull(await db.Markers.AsNoTracking().FirstOrDefaultAsync(m => m.id == Guid.Parse(other.id!)));
  }

  [Fact]
  public async Task MovingTwoNodesInOneWriteIsCheckedAgainstWhereTheyWillBe()
  {
    var (db, map) = CreateDb();
    var first = new BaseMarkerDTO { name = "first" };
    var second = new BaseMarkerDTO { name = "second" };
    await map.UpdateHierarchyAsync([first, second]);

    // First goes under Second while Second goes under First: together that is a loop.
    first.parent_id = second.id;
    second.parent_id = first.id;
    await Assert.ThrowsAsync<ArgumentException>(() => map.UpdateHierarchyAsync([first, second]));

    Assert.Null((await db.Markers.AsNoTracking().SingleAsync(m => m.id == Guid.Parse(first.id!))).parent_id);
  }

  [Fact]
  public async Task MovingAContainerRecomputesTheCanvasOfItsWholeSubtree()
  {
    var (db, map) = CreateDb();
    var first = Space(db, "first map");
    var second = Space(db, "second map");

    var container = new BaseMarkerDTO { name = "district", parent_id = first.ToString() };
    await map.UpdateHierarchyAsync([container]);
    var building = new BaseMarkerDTO { name = BuildingLabel, parent_id = container.id };
    await map.UpdateHierarchyAsync([building]);
    var camera = new BaseMarkerDTO { name = CameraLabel, parent_id = building.id };
    await map.UpdateHierarchyAsync([camera]);

    var geo = new GeoService(db, new LevelService(db));
    await geo.CreateGeo([
      Point(container.id!, 0.5, 0.5),
      Point(building.id!, 0.5, 0.5),
      Point(camera.id!, 0.5, 0.5),
    ]);
    Assert.All(
      await db.GeoObjects.ToListAsync(),
      figure => Assert.Equal(first, figure.canvas_id));

    // The container moves to another map, and its children go with it.
    container.parent_id = second.ToString();
    await map.UpdateHierarchyAsync([container]);

    Assert.All(
      await db.GeoObjects.ToListAsync(),
      figure => Assert.Equal(second, figure.canvas_id));
  }

  private static Guid Space(VmsMapDbContext db, string name, Guid? parent = null)
  {
    var id = Guid.NewGuid();
    db.Markers.Add(new DBMarker { id = id, name = name, parent_id = parent });
    db.Spaces.Add(new DBSpace { object_id = id, kind = SpaceKinds.Map });
    db.SaveChanges();
    return id;
  }

  private static FigureGeoDTO Point(string id, double lat, double lon) => new()
  {
    id = id,
    geometry = new GeometryCircleDTO(new Geo2DCoordDTO { Lat = lat, Lon = lon }),
  };

  [Fact]
  public async Task UpdateHierarchyAssignsAnIdToANewNode()
  {
    var (db, map) = CreateDb();

    var node = new BaseMarkerDTO { name = CameraLabel };
    await map.UpdateHierarchyAsync(new[] { node });

    Assert.False(string.IsNullOrEmpty(node.id));
    Assert.Equal(1, await db.Markers.CountAsync());
  }

  [Fact]
  public async Task GetOwnersAndViewsKeepsPlainObjects()
  {
    var (db, map) = CreateDb();

    var owner = new BaseMarkerDTO { name = OwnerLabel };
    await map.UpdateHierarchyAsync(new[] { owner });

    var view = new BaseMarkerDTO { name = ViewLabel, owner_id = owner.id };
    var plain = new BaseMarkerDTO { name = PlainLabel };
    await map.UpdateHierarchyAsync(new[] { view, plain });

    // Square returned only the replicas and the owners fetched for them, so `plain` vanished.
    var result = await map.GetOwnersAndViewsAsync(new List<string>
    {
      view.id!, plain.id!
    });

    Assert.Equal(3, result.Count);
    Assert.Contains(result, kv => kv.Value.name == PlainLabel);
    Assert.Contains(result, kv => kv.Value.name == OwnerLabel);
    Assert.Contains(result, kv => kv.Value.name == ViewLabel);
  }

  [Fact]
  public async Task FileServiceStoresAndReturnsTheBytes()
  {
    var (db, _) = CreateDb();
    var files = new FileService(db);

    var bytes = new byte[] { 1, 2, 3, 4 };
    var stored = await files.PutAsync(PngMime, bytes);

    var loaded = await files.GetAsync(Utils.ConvertGuidToObjectId(stored.id));

    Assert.NotNull(loaded);
    Assert.Equal(bytes, loaded!.bytes);
    Assert.Equal(PngMime, loaded.mime);
    Assert.False(string.IsNullOrEmpty(loaded.etag));
  }

  [Fact]
  public async Task FileServiceRejectsAFileAboveTheLimit()
  {
    var (db, _) = CreateDb();
    var files = new FileService(db);

    var tooBig = new byte[FileService.MaxFileBytes + 1];

    await Assert.ThrowsAsync<ArgumentException>(() => files.PutAsync(PngMime, tooBig));
  }

  [Fact]
  public async Task VisualServiceUpdatesAndReadsBack()
  {
    var (db, _) = CreateDb();
    var visuals = new VisualService(db);

    var node = new BaseMarkerDTO { name = CameraLabel };
    var map = new MapDb(db);
    await map.UpdateHierarchyAsync(new[] { node });

    await visuals.UpdateListAsync(new List<VisualDTO>
    {
      new() { object_id = node.id, color = VisualColor, rotation_deg = 90, text = "cam" }
    });

    var loaded = await visuals.GetListByIdsAsync(new List<string> { node.id! });

    Assert.Equal(VisualColor, loaded[node.id!].color);
    Assert.Equal(90, loaded[node.id!].rotation_deg);
  }

  // ── Tree invariants ───────────────────────────────────────────────────────────────────────

  [Fact]
  public async Task AnOriginalWithPicturesCannotBecomeAPicture()
  {
    var (db, map) = CreateDb();

    var original = new BaseMarkerDTO { name = CameraLabel };
    var other = new BaseMarkerDTO { name = "other camera" };
    await map.UpdateHierarchyAsync([original, other]);
    await map.UpdateHierarchyAsync([new BaseMarkerDTO { name = CameraLabel, owner_id = original.id }]);

    // A picture of a picture would make everything that resolves an owner in one hop -- states,
    // values, camera id, node kind, rights -- answer with what the new owner does not have.
    original.owner_id = other.id;
    await Assert.ThrowsAsync<ArgumentException>(() => map.UpdateHierarchyAsync([original]));

    Assert.Null((await db.Markers.AsNoTracking()
      .SingleAsync(m => m.id == Guid.Parse(original.id!))).owner_id);
  }

  [Fact]
  public async Task ANodeWithAMissingParentIsRefused()
  {
    var (db, map) = CreateDb();

    var orphan = new BaseMarkerDTO { name = "orphan", parent_id = Guid.NewGuid().ToString() };
    await Assert.ThrowsAsync<ArgumentException>(() => map.UpdateHierarchyAsync([orphan]));
    Assert.Empty(await db.Markers.ToListAsync());

    // A move to a node that does not exist is refused too, and the node stays where it was.
    var node = new BaseMarkerDTO { name = BuildingLabel };
    await map.UpdateHierarchyAsync([node]);
    node.parent_id = Guid.NewGuid().ToString();
    await Assert.ThrowsAsync<ArgumentException>(() => map.UpdateHierarchyAsync([node]));
    Assert.Null((await db.Markers.AsNoTracking()
      .SingleAsync(m => m.id == Guid.Parse(node.id!))).parent_id);
  }

  [Fact]
  public async Task AReplicaWhoseOwnerDoesNotExistIsRefused()
  {
    var (db, map) = CreateDb();

    var replica = new BaseMarkerDTO
    {
      name = CameraLabel,
      owner_id = Guid.NewGuid().ToString(),
    };
    await Assert.ThrowsAsync<ArgumentException>(() => map.UpdateHierarchyAsync([replica]));
    Assert.Empty(await db.Markers.ToListAsync());
  }

  [Fact]
  public async Task AParentAndAnOwnerCreatedInTheSameWriteAreAllowed()
  {
    var (db, map) = CreateDb();

    var parentId = Guid.NewGuid().ToString();
    var originalId = Guid.NewGuid().ToString();
    var pictureId = Guid.NewGuid().ToString();

    await map.UpdateHierarchyAsync([
      new BaseMarkerDTO { id = parentId, name = BuildingLabel },
      new BaseMarkerDTO { id = originalId, parent_id = parentId, name = CameraLabel },
      new BaseMarkerDTO
      {
        id = pictureId,
        parent_id = parentId,
        owner_id = originalId,
        name = "camera picture",
      },
    ]);

    Assert.Equal(3, await db.Markers.CountAsync());
  }

  [Fact]
  public async Task RenamingANodeWithAnAlreadyDanglingParentStillWorks()
  {
    var (db, map) = CreateDb();

    // Nothing checks the parents already stored: they may predate the rule, and a rename must not
    // be the thing that fails on them.
    var node = Guid.NewGuid();
    var dangling = Guid.NewGuid();
    db.Markers.Add(new DBMarker
    {
      id = node,
      parent_id = dangling,
      name = "legacy",
    });
    await db.SaveChangesAsync();

    await map.UpdateHierarchyAsync([
      new BaseMarkerDTO { id = node.ToString(), parent_id = dangling.ToString(), name = RenamedLabel }]);

    var stored = await db.Markers.AsNoTracking().SingleAsync(m => m.id == node);
    Assert.Equal(RenamedLabel, stored.name);
  }

  [Fact]
  public async Task AMapCannotBeMovedUnderAnotherNode()
  {
    var (db, map) = CreateDb();

    var building = new BaseMarkerDTO { name = BuildingLabel };
    await map.UpdateHierarchyAsync([building]);

    var spaceId = Guid.NewGuid();
    db.Markers.Add(new DBMarker { id = spaceId, name = MainMapName });
    db.Spaces.Add(new DBSpace { object_id = spaceId, kind = SpaceKinds.Map });
    await db.SaveChangesAsync();

    await Assert.ThrowsAsync<ArgumentException>(() => map.UpdateHierarchyAsync([
      new BaseMarkerDTO { id = spaceId.ToString(), parent_id = building.id, name = MainMapName }]));

    Assert.Null((await db.Markers.AsNoTracking().SingleAsync(m => m.id == spaceId)).parent_id);
  }

  [Fact]
  public async Task APlanMustStandInsideASpace()
  {
    var (db, map) = CreateDb();

    var building = new BaseMarkerDTO { name = BuildingLabel };
    await map.UpdateHierarchyAsync([building]);

    var mapId = Guid.NewGuid();
    var planId = Guid.NewGuid();
    db.Markers.AddRange(
      new DBMarker { id = mapId, name = MainMapName },
      new DBMarker { id = planId, name = FloorPlanName });
    db.Spaces.Add(new DBSpace { object_id = mapId, kind = SpaceKinds.Map });
    db.Spaces.Add(new DBSpace { object_id = planId, kind = SpaceKinds.Plan });
    await db.SaveChangesAsync();

    await Assert.ThrowsAsync<ArgumentException>(() => map.UpdateHierarchyAsync([
      new BaseMarkerDTO { id = planId.ToString(), parent_id = building.id, name = FloorPlanName }]));
    Assert.Null((await db.Markers.AsNoTracking().SingleAsync(m => m.id == planId)).parent_id);

    // The model's own shape is allowed: a plan lies in a space.
    await map.UpdateHierarchyAsync([
      new BaseMarkerDTO { id = planId.ToString(), parent_id = mapId.ToString(), name = FloorPlanName }]);
    Assert.Equal(mapId, (await db.Markers.AsNoTracking().SingleAsync(m => m.id == planId)).parent_id);
  }

  [Fact]
  public async Task ACameraNodeStaysOutOfSpaces()
  {
    var (db, map) = CreateDb();

    var server = new BaseMarkerDTO { name = "srv" };
    await map.UpdateHierarchyAsync([server]);

    var spaceId = Guid.NewGuid();
    db.Markers.Add(new DBMarker { id = spaceId, name = MainMapName });
    db.Spaces.Add(new DBSpace { object_id = spaceId, kind = SpaceKinds.Map });

    var cameraId = Guid.Parse(CameraObjectId.For(TestCameras.One));
    db.Markers.Add(new DBMarker
    {
      id = cameraId,
      parent_id = Guid.Parse(server.id!),
      name = CameraLabel,
    });
    db.DeviceNodes.Add(new DBDeviceNode
    {
      object_id = cameraId,
      source_kind = DeviceObjectId.CameraSource,
      source_id = TestCameras.One,
      part_kind = DeviceObjectId.DevicePart,
    });
    await db.SaveChangesAsync();

    await Assert.ThrowsAsync<ArgumentException>(() => map.UpdateHierarchyAsync([
      new BaseMarkerDTO { id = cameraId.ToString(), parent_id = spaceId.ToString(), name = CameraLabel }]));
    Assert.Equal(Guid.Parse(server.id!),
      (await db.Markers.AsNoTracking().SingleAsync(m => m.id == cameraId)).parent_id);

    // Under the server is where it stands.
    await map.UpdateHierarchyAsync([
      new BaseMarkerDTO { id = cameraId.ToString(), parent_id = server.id, name = CameraLabel }]);
  }
}
