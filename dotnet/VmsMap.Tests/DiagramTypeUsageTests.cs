using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using VmsMap.Db;
using VmsMap.Domain;
using VmsMap.Grpc;

namespace VmsMap.Tests;

// Where a type is in use is derived, never stored: the diagrams drawn from it and the device
// bindings that keep one. A row is a place, so a diagram shown on another canvas is listed twice —
// once where its node stands and once where its replica does.
public class DiagramTypeUsageTests
{
  // The fixtures these tests share.
  private const string TypeName = "rear panel";
  private const string OtherTypeName = "front panel";
  private const string DiagramName = "panel a";
  private const string ReplicaName = "panel a rep1";
  private const string DeviceDiagramName = "cam1 / rear panel";
  private const string ServerRoom = "server room";
  private const string Warehouse = "warehouse";

  private static VmsMapDbContext NewDb() => new(
    new DbContextOptionsBuilder<VmsMapDbContext>()
      .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
      .Options);

  private static async Task<DiagramTypeDTO> SaveTypeAsync(
    DiagramTypeService service, string name)
  {
    var type = new DiagramTypeDTO
    {
      name = name,
      regions =
      [
        new DiagramTypeRegionDTO
        {
          region_key = "caption",
          geometry = new DiagramCoordDTO { width = 10, height = 10 },
        },
      ],
    };
    await service.UpdateListAsync([type]);
    return type;
  }

  private static Guid IdOf(DiagramTypeDTO type) =>
    Utils.ConvertObjectIdToGuid(type.id)!.Value;

  /// A space: a marker plus the side row that makes it a canvas the resolver recognizes.
  private static Guid AddSpace(VmsMapDbContext db, string name, Guid? parent = null)
  {
    var id = Guid.NewGuid();
    db.Markers.Add(new DBMarker { id = id, name = name, parent_id = parent });
    db.Spaces.Add(new DBSpace { object_id = id, kind = SpaceKinds.Map });
    return id;
  }

  private static Guid AddNode(
    VmsMapDbContext db, string name, Guid? parent = null, Guid? owner = null, Guid? type = null)
  {
    var id = Guid.NewGuid();
    db.Markers.Add(new DBMarker { id = id, name = name, parent_id = parent, owner_id = owner });
    if (type is { } named)
      db.Diagrams.Add(new DBDiagram { id = id, dgr_type = named });
    return id;
  }

  [Fact]
  public async Task ATypeInUseListsEveryPlaceItIsShown()
  {
    var db = NewDb();
    var service = new DiagramTypeService(db);
    var type = await SaveTypeAsync(service, TypeName);
    var other = await SaveTypeAsync(service, OtherTypeName);

    var serverRoom = AddSpace(db, ServerRoom);
    var warehouse = AddSpace(db, Warehouse);

    // An ordinary diagram on the first space and the same diagram shown on the second.
    var drawn = AddNode(db, DiagramName, parent: serverRoom, type: IdOf(type));
    AddNode(db, ReplicaName, parent: warehouse, owner: drawn);

    // A device's diagram hangs under the camera's picture; the space is its canvas, the camera
    // is not. The camera picture stands on the first space.
    var camera = AddNode(db, "cam1", parent: serverRoom);
    var deviceDiagram = AddNode(db, DeviceDiagramName, parent: camera, type: IdOf(type));
    db.DeviceDiagrams.Add(new DBDeviceDiagram
    {
      object_id = deviceDiagram,
      source_kind = DeviceObjectId.CameraSource,
      source_id = TestCameras.One,
      slot = IDeviceDiagramService.DefaultSlot,
      diagram_type_id = IdOf(type),
    });

    // A diagram of another type is not this type's business.
    AddNode(db, "another panel", parent: serverRoom, type: IdOf(other));
    await db.SaveChangesAsync();

    var rows = await DiagramTypeUsage.ResolveAsync(db, IdOf(type), CancellationToken.None);

    var ordinary = rows.Where(row => !row.IsDevice).ToList();
    Assert.Equal(2, ordinary.Count);

    var own = ordinary.Single(row => row.Name == DiagramName);
    Assert.False(own.IsRepresentation);
    Assert.Equal(ServerRoom, own.CanvasName);

    var replica = ordinary.Single(row => row.Name == ReplicaName);
    Assert.True(replica.IsRepresentation);
    Assert.Equal(Warehouse, replica.CanvasName);

    var device = Assert.Single(rows, row => row.IsDevice);
    Assert.Equal(DeviceDiagramName, device.Name);
    Assert.Equal(TestCameras.One, device.SourceId);
    Assert.Equal(ServerRoom, device.CanvasName);
  }

  [Fact]
  public async Task AnUnusedTypeListsNothing()
  {
    var db = NewDb();
    var service = new DiagramTypeService(db);
    var type = await SaveTypeAsync(service, TypeName);

    var rows = await DiagramTypeUsage.ResolveAsync(db, IdOf(type), CancellationToken.None);

    Assert.Empty(rows);
  }

  [Fact]
  public async Task ATypeTheCallerNamesByNoKnownIdListsNothing()
  {
    var db = NewDb();

    var rows = await DiagramTypeUsage.ResolveAsync(db, Guid.NewGuid(), CancellationToken.None);

    Assert.Empty(rows);
  }
}
