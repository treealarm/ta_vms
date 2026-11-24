using System;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using VmsMap.Db;
using VmsMap.Domain;

namespace VmsMap.Tests;

// What one placement costs the database, in statements. The number is not a budget to spend down —
// it is a contract: placing a device's panel on a mnemoscheme is an administrator's action that
// runs under the map's locks, and every statement added to it is paid while the deployment waits.
// The test is here rather than beside the others because a statement count is only observable on a
// real provider; it is skipped without VMS_MAP_TEST_DSN, as the other Postgres tests are.
[Collection("postgis")]
public class DeviceDiagramPlacementPostgresTests
{
  // The fixtures these tests share.
  private const string CameraId = "3f2504e0-4f89-11d3-9a0c-0305e82c3301";
  private const string CanvasName = "cam diagrams";
  private const string LeftRegion = "left";
  private const string PlanName = "plan";
  private const string CameraLabel = "camera";
  private const string PanelLabel = "camera / panel";

  /// <summary>
  /// The statements one placement on a region costs: the diagram already exists, the canvas does
  /// too, and nothing about the device is configured, so this is the cheapest real placement there
  /// is. Changing it is a decision, not an accident.
  /// </summary>
  private const int PlacementStatements = 27;

  /// <summary>
  /// Every command the context runs, whether it reads rows, asks for a value or writes. The async
  /// and synchronous hooks are counted separately: this provider reaches both, depending on the
  /// call, and one hook does not stand for the other.
  /// </summary>
  private sealed class StatementCounter : DbCommandInterceptor
  {
    public int Count { get; private set; }

    public void Reset() => Count = 0;

    public override InterceptionResult<DbDataReader> ReaderExecuting(
      DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
      Count++;
      return result;
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
      DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
      CancellationToken cancellationToken = default)
    {
      Count++;
      return ValueTask.FromResult(result);
    }

    public override InterceptionResult<int> NonQueryExecuting(
      DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
      Count++;
      return result;
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
      DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
      CancellationToken cancellationToken = default)
    {
      Count++;
      return ValueTask.FromResult(result);
    }

    public override InterceptionResult<object> ScalarExecuting(
      DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
    {
      Count++;
      return result;
    }

    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
      DbCommand command, CommandEventData eventData, InterceptionResult<object> result,
      CancellationToken cancellationToken = default)
    {
      Count++;
      return ValueTask.FromResult(result);
    }
  }

  /// <summary>
  /// A plan the camera stands on, its diagram hanging under the camera's picture, and a
  /// mnemoscheme whose type offers one region.
  /// </summary>
  private static async Task<(Guid Canvas, string Canonical)> SeedAsync(VmsMapDbContext db)
  {
    var map = new MapDb(db);

    var space = Guid.NewGuid();
    await map.UpdateHierarchyAsync([new BaseMarkerDTO { id = space.ToString(), name = PlanName }]);
    db.Spaces.Add(new DBSpace
    {
      object_id = space,
      kind = SpaceKinds.Map,
      view_south = 0,
      view_north = 1,
      view_west = 0,
      view_east = 1,
    });

    var picture = Guid.NewGuid();
    await map.UpdateHierarchyAsync([
      new BaseMarkerDTO { id = picture.ToString(), parent_id = space.ToString(), name = CameraLabel }
    ]);
    db.CameraPlacements.Add(new DBCameraPlacement
    {
      object_id = picture,
      camera_id = CameraId,
      is_reference = true,
    });

    var canonical = DeviceObjectId.ForDeviceDiagram(
      DeviceObjectId.CameraSource, CameraId, IDeviceDiagramService.DefaultSlot);
    var canonicalId = Utils.ConvertObjectIdToGuid(canonical)!.Value;
    await map.UpdateHierarchyAsync([
      new BaseMarkerDTO { id = canonical, parent_id = picture.ToString(), name = PanelLabel }
    ]);
    db.Diagrams.Add(new DBDiagram { id = canonicalId });
    db.DeviceDiagrams.Add(new DBDeviceDiagram
    {
      object_id = canonicalId,
      source_kind = DeviceObjectId.CameraSource,
      source_id = CameraId,
      slot = IDeviceDiagramService.DefaultSlot,
    });

    var type = new DBDiagramType { id = Guid.NewGuid(), name = CanvasName };
    type.regions.Add(new DBDiagramTypeRegion
    {
      diagram_type_id = type.id,
      region_key = LeftRegion,
      geometry = new DBDiagramCoord { width = 0.5, height = 0.5 },
    });
    db.DiagramTypes.Add(type);

    var canvas = Guid.NewGuid();
    await map.UpdateHierarchyAsync([new BaseMarkerDTO { id = canvas.ToString(), name = CanvasName }]);
    db.Diagrams.Add(new DBDiagram { id = canvas, dgr_type = type.id });

    await db.SaveChangesAsync();
    return (canvas, canonical);
  }

  [Fact]
  public async Task PlacingAPanelOnARegionCostsAFixedNumberOfStatements()
  {
    var counter = new StatementCounter();
    await using var connection = await MapPostgres.OpenAsync(counter, fresh: true);
    if (connection is null)
      return;

    var diagrams = MapPostgres.Diagrams(connection.Db);
    var (canvas, _) = await SeedAsync(connection.Db);

    counter.Reset();

    var shown = await diagrams.PlaceDeviceDiagramAsync(
      DeviceObjectId.CameraSource, CameraId, null, IDeviceDiagramService.DefaultSlot,
      new DiagramTarget(canvas.ToString(), RegionId: LeftRegion), default);

    Assert.NotNull(shown);
    Assert.Equal(PlacementStatements, counter.Count);
  }

  [Fact]
  public async Task TwoPlacementsOfOnePanelInOneRegionMakeOneRepresentation()
  {
    await using var first = await MapPostgres.OpenAsync(fresh: true);
    if (first is null)
      return;

    var (canvas, _) = await SeedAsync(first.Db);

    // Two connections, as two replicas of the service would have: the device lock is what keeps
    // them from each making a copy — a region holds one node, and "already there" is not a refusal.
    await using var second = await MapPostgres.OpenAsync();
    if (second is null)
      return;

    var target = new DiagramTarget(canvas.ToString(), RegionId: LeftRegion);
    var placed = await Task.WhenAll(
      MapPostgres.Diagrams(first.Db).PlaceDeviceDiagramAsync(
        DeviceObjectId.CameraSource, CameraId, null, IDeviceDiagramService.DefaultSlot, target, default),
      MapPostgres.Diagrams(second.Db).PlaceDeviceDiagramAsync(
        DeviceObjectId.CameraSource, CameraId, null, IDeviceDiagramService.DefaultSlot, target, default));

    Assert.NotNull(placed[0]);
    Assert.Equal(placed[0], placed[1]);
    Assert.Equal(
      1, await first.Db.Markers.CountAsync(m => m.parent_id == canvas && m.owner_id != null));
  }
}
