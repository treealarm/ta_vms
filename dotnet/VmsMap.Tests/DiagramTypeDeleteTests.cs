using System;
using System.Threading.Tasks;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using VmsMap.Db;
using VmsMap.Domain;
using MapProto = Vms.Map;

namespace VmsMap.Tests;

// A type is a dictionary entry that diagrams and device bindings name. Taking it away while one of
// them still does would leave those rows without the regions their children stand in and without
// the template their parts are drawn from — so the dictionary refuses, the third side of the rule
// that already guards a type's regions and a diagram's type.
public class DiagramTypeDeleteTests
{
  // The fixtures these tests share.
  private const string TypeName = "rear panel";
  private const string OtherTypeName = "front panel";
  private const string DiagramName = "panel";
  private const string CaptionRegion = "caption";

  private static VmsMapDbContext NewDb() => new(
    new DbContextOptionsBuilder<VmsMapDbContext>()
      .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
      .Options);

  private static DiagramTypeDTO Type(string name) => new()
  {
    name = name,
    regions =
    [
      new DiagramTypeRegionDTO
      {
        region_key = CaptionRegion,
        geometry = new DiagramCoordDTO { width = 10, height = 10 },
      },
    ],
  };

  private static async Task<DiagramTypeDTO> SaveTypeAsync(
    DiagramTypeService service, string name = TypeName)
  {
    var type = Type(name);
    await service.UpdateListAsync([type]);
    return type;
  }

  private static Guid IdOf(DiagramTypeDTO type) =>
    Utils.ConvertObjectIdToGuid(type.id)!.Value;

  [Fact]
  public async Task ADiagramDrawnFromTheTypeCannotBeDeleted()
  {
    var db = NewDb();
    var service = new DiagramTypeService(db);
    var type = await SaveTypeAsync(service);

    var diagram = Guid.NewGuid();
    db.Markers.Add(new DBMarker { id = diagram, name = DiagramName });
    db.Diagrams.Add(new DBDiagram { id = diagram, dgr_type = IdOf(type) });
    await db.SaveChangesAsync();

    var error = await Assert.ThrowsAsync<DiagramTypeConfigException>(
      () => service.DeleteAsync([type.id!]));

    Assert.Contains(Utils.ConvertGuidToObjectId(diagram), error.Message, StringComparison.Ordinal);
    Assert.Single(db.DiagramTypes);
  }

  [Fact]
  public async Task ADiagramKeptForADeviceCannotBeDeleted()
  {
    var db = NewDb();
    var service = new DiagramTypeService(db);
    var type = await SaveTypeAsync(service);

    // A device's diagram is a diagram too, but the binding is what keeps it in step with the
    // template, so the refusal must reach it even when no diagram row names the type. The node
    // exists, as it does in production: a binding is removed with its node by the database.
    var diagram = Guid.NewGuid();
    db.Markers.Add(new DBMarker { id = diagram, name = DiagramName });
    db.DeviceDiagrams.Add(new DBDeviceDiagram
    {
      object_id = diagram,
      source_kind = DeviceObjectId.CameraSource,
      source_id = TestCameras.One,
      slot = IDeviceDiagramService.DefaultSlot,
      diagram_type_id = IdOf(type),
    });
    await db.SaveChangesAsync();

    var error = await Assert.ThrowsAsync<DiagramTypeConfigException>(
      () => service.DeleteAsync([type.id!]));

    Assert.Contains(TestCameras.One, error.Message, StringComparison.Ordinal);
    Assert.Single(db.DiagramTypes);
  }

  [Fact]
  public async Task DeletingOneOfSeveralTypesChecksOnlyItsOwnReferences()
  {
    var db = NewDb();
    var service = new DiagramTypeService(db);
    var free = await SaveTypeAsync(service, OtherTypeName);
    var used = await SaveTypeAsync(service, TypeName);

    var drawn = Guid.NewGuid();
    db.Markers.Add(new DBMarker { id = drawn, name = DiagramName });
    db.Diagrams.Add(new DBDiagram { id = drawn, dgr_type = IdOf(used) });
    await db.SaveChangesAsync();

    // The free type is asked for first and passes its own check; the used one refuses the call.
    // One SaveChanges for the request means the free type is not deleted either.
    await Assert.ThrowsAsync<DiagramTypeConfigException>(
      () => service.DeleteAsync([free.id!, used.id!]));

    Assert.Equal(2, db.DiagramTypes.Count());
  }

  [Fact]
  public async Task AFreeTypeIsDeleted()
  {
    var db = NewDb();
    var service = new DiagramTypeService(db);
    var type = await SaveTypeAsync(service);

    await service.DeleteAsync([type.id!]);

    Assert.Empty(db.DiagramTypes);
  }

  [Fact]
  public async Task TheRefusalReachesTheRpcCallerAsInvalidArgument()
  {
    var db = NewDb();
    var service = new DiagramTypeService(db);
    var type = await SaveTypeAsync(service);

    var diagram = Guid.NewGuid();
    db.Markers.Add(new DBMarker { id = diagram, name = DiagramName });
    db.Diagrams.Add(new DBDiagram { id = diagram, dgr_type = IdOf(type) });
    await db.SaveChangesAsync();

    // DeleteDiagramTypes does not read the call context, so the stub the other write tests use
    // would only add noise here.
    var error = await Assert.ThrowsAsync<RpcException>(() =>
      TestWiring.MapRpc(db).DeleteDiagramTypes(
        new MapProto.ObjectIds { Ids = { type.id! } }, null!));

    Assert.Equal(StatusCode.InvalidArgument, error.StatusCode);
    Assert.Contains(TypeName, error.Status.Detail, StringComparison.Ordinal);
    Assert.Single(db.DiagramTypes);
  }
}
