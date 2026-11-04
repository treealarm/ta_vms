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

    var parent = new BaseMarkerDTO { name = "Building" };
    var child = new BaseMarkerDTO { name = "Floor 1", parent_id = null };
    await map.UpdateHierarchyAsync(new[] { parent, child });

    var otherParent = new BaseMarkerDTO { name = "Building 2" };
    await map.UpdateHierarchyAsync(new[] { otherParent });

    // Square's UpdateHierarchyAsync called Update() on the unchanged entity: this rename and
    // move were silently dropped.
    child.name = "Floor 2";
    child.parent_id = otherParent.id;
    await map.UpdateHierarchyAsync(new[] { child });

    var stored = await db.Markers.SingleAsync(m => m.name == "Floor 2");
    Assert.Equal(Utils.ConvertObjectIdToGuid(otherParent.id), stored.parent_id);
  }

  [Fact]
  public async Task MovingANodeUnderItsOwnDescendantIsRefused()
  {
    var (db, map) = CreateDb();
    var container = new BaseMarkerDTO { name = "Container" };
    await map.UpdateHierarchyAsync([container]);
    var child = new BaseMarkerDTO { name = "Child", parent_id = container.id };
    await map.UpdateHierarchyAsync([child]);
    var grandchild = new BaseMarkerDTO { name = "Grandchild", parent_id = child.id };
    await map.UpdateHierarchyAsync([grandchild]);

    container.parent_id = grandchild.id;
    await Assert.ThrowsAsync<ArgumentException>(() => map.UpdateHierarchyAsync([container]));

    // Nothing was written: the tree still has the container at the root.
    Assert.Null((await db.Markers.AsNoTracking().SingleAsync(m => m.id == Guid.Parse(container.id!))).parent_id);
    Assert.Equal(Guid.Parse(container.id!), (await db.Markers.AsNoTracking().SingleAsync(m => m.id == Guid.Parse(child.id!))).parent_id);
  }

  [Fact]
  public async Task DeletingANodeRemovesItsReplicasAndNothingElse()
  {
    var (db, map) = CreateDb();

    var owner = new BaseMarkerDTO { name = "Rack" };
    await map.UpdateHierarchyAsync([owner]);
    var replica = new BaseMarkerDTO { name = "Rack", owner_id = owner.id };
    await map.UpdateHierarchyAsync([replica]);
    var other = new BaseMarkerDTO { name = "Other" };
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
    var first = new BaseMarkerDTO { name = "First" };
    var second = new BaseMarkerDTO { name = "Second" };
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
    var first = Space(db, "First map");
    var second = Space(db, "Second map");

    var container = new BaseMarkerDTO { name = "District", parent_id = first.ToString() };
    await map.UpdateHierarchyAsync([container]);
    var building = new BaseMarkerDTO { name = "Building", parent_id = container.id };
    await map.UpdateHierarchyAsync([building]);
    var camera = new BaseMarkerDTO { name = "Camera", parent_id = building.id };
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

    var node = new BaseMarkerDTO { name = "Camera" };
    await map.UpdateHierarchyAsync(new[] { node });

    Assert.False(string.IsNullOrEmpty(node.id));
    Assert.Equal(1, await db.Markers.CountAsync());
  }

  [Fact]
  public async Task GetOwnersAndViewsKeepsPlainObjects()
  {
    var (db, map) = CreateDb();

    var owner = new BaseMarkerDTO { name = "Owner" };
    await map.UpdateHierarchyAsync(new[] { owner });

    var view = new BaseMarkerDTO { name = "View", owner_id = owner.id };
    var plain = new BaseMarkerDTO { name = "Plain" };
    await map.UpdateHierarchyAsync(new[] { view, plain });

    // Square returned only the replicas and the owners fetched for them, so `plain` vanished.
    var result = await map.GetOwnersAndViewsAsync(new List<string>
    {
      view.id!, plain.id!
    });

    Assert.Equal(3, result.Count);
    Assert.Contains(result, kv => kv.Value.name == "Plain");
    Assert.Contains(result, kv => kv.Value.name == "Owner");
    Assert.Contains(result, kv => kv.Value.name == "View");
  }

  [Fact]
  public async Task FileServiceStoresAndReturnsTheBytes()
  {
    var (db, _) = CreateDb();
    var files = new FileService(db);

    var bytes = new byte[] { 1, 2, 3, 4 };
    var stored = await files.PutAsync("image/png", bytes);

    var loaded = await files.GetAsync(Utils.ConvertGuidToObjectId(stored.id));

    Assert.NotNull(loaded);
    Assert.Equal(bytes, loaded!.bytes);
    Assert.Equal("image/png", loaded.mime);
    Assert.False(string.IsNullOrEmpty(loaded.etag));
  }

  [Fact]
  public async Task FileServiceRejectsAFileAboveTheLimit()
  {
    var (db, _) = CreateDb();
    var files = new FileService(db);

    var tooBig = new byte[FileService.MaxFileBytes + 1];

    await Assert.ThrowsAsync<ArgumentException>(() => files.PutAsync("image/png", tooBig));
  }

  [Fact]
  public async Task VisualServiceUpdatesAndReadsBack()
  {
    var (db, _) = CreateDb();
    var visuals = new VisualService(db);

    var node = new BaseMarkerDTO { name = "Camera" };
    var map = new MapDb(db);
    await map.UpdateHierarchyAsync(new[] { node });

    await visuals.UpdateListAsync(new List<VisualDTO>
    {
      new() { object_id = node.id, color = "#ff0000", rotation_deg = 90, text = "cam" }
    });

    var loaded = await visuals.GetListByIdsAsync(new List<string> { node.id! });

    Assert.Equal("#ff0000", loaded[node.id!].color);
    Assert.Equal(90, loaded[node.id!].rotation_deg);
  }
}
