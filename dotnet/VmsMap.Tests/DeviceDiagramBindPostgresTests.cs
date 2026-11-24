using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using VmsMap.Clients;
using VmsMap.Db;
using VmsMap.Domain;

namespace VmsMap.Tests;

// The races a hand-made binding takes part in: the sweep reconciling the same device, the camera
// going away, and two administrators binding one diagram at once. Two connections are two replicas
// of VmsMap, and the advisory locks are only observable on a real provider; the tests are skipped
// without VMS_MAP_TEST_DSN, as the other Postgres tests are.
[Collection("postgis")]
public class DeviceDiagramBindPostgresTests
{
  private const string CameraId = "3f2504e0-4f89-11d3-9a0c-0305e82c3301";
  private const string OtherCameraId = "3f2504e0-4f89-11d3-9a0c-0305e82c3302";
  private const string Manufacturer = "OnvifSim";
  private const string Model = "Simulated fixed camera";
  private const string PanelTypeName = "rear panel";
  private const string RelayToken = "lamp";
  private const string RelayRegion = "lamp";
  private const string RelayName = "Lamp";
  private const string SpareName = "spare panel";
  private const string ArmModeInherit = "inherit";

  /// <summary>A stand-in for VmsCfg: whether it still knows a camera is the test's to say.</summary>
  private sealed class TestCatalog : ICameraCatalog
  {
    public Func<string, bool> Known { get; set; } = _ => true;

    public Task<CameraInfo?> GetAsync(string cameraId, CancellationToken ct) =>
      Task.FromResult(
        Known(cameraId) ? new CameraInfo(cameraId, "Camera " + cameraId, "srv") : null);

    public Task<List<CameraInfo>> ListAsync(string? serverId, CancellationToken ct) =>
      Task.FromResult(new List<CameraInfo>());
  }

  /// <summary>One configured relay: a part for a binding to draw, and for the sweep to keep.</summary>
  private sealed class OneRelay : IDeviceSettingsSource
  {
    private static readonly IReadOnlyList<DeviceSettingsInfo> Rows =
      [new(CameraId, DeviceObjectId.RelayPart, RelayToken, RelayName, ArmModeInherit, null, false)];

    public Task<IReadOnlyList<DeviceSettingsInfo>> ListAsync(string cameraId, CancellationToken ct) =>
      Task.FromResult(cameraId == CameraId ? Rows : (IReadOnlyList<DeviceSettingsInfo>)[]);

    public Task<DeviceSettingsInfo?> FindAsync(
      string cameraId, string kind, string token, CancellationToken ct) =>
      Task.FromResult(cameraId == CameraId && kind == DeviceObjectId.RelayPart && token == RelayToken
        ? Rows[0]
        : null);

    public Task<IReadOnlyDictionary<string, IReadOnlyList<DeviceSettingsInfo>>> ListManyAsync(
      IReadOnlyCollection<string> cameraIds, CancellationToken ct) =>
      Task.FromResult<IReadOnlyDictionary<string, IReadOnlyList<DeviceSettingsInfo>>>(
        cameraIds.Distinct(StringComparer.Ordinal).ToDictionary(
          id => id,
          id => id == CameraId ? Rows : (IReadOnlyList<DeviceSettingsInfo>)[],
          StringComparer.Ordinal));

    public void Invalidate(string cameraId) { }
  }

  private static async Task<Exception?> Capture(Func<Task> action)
  {
    try
    {
      await action();
      return null;
    }
    catch (Exception ex)
    {
      return ex;
    }
  }

  /// <summary>
  /// The camera's model, the template that describes it with one part-bound region, the part's node
  /// — and an ordinary diagram of the administrator's, the one to be bound.
  /// </summary>
  private static async Task<string> SeedAsync(VmsMapDbContext db)
  {
    db.DeviceInfos.Add(new DBDeviceInfo
    {
      source_kind = DeviceObjectId.CameraSource,
      source_id = CameraId,
      manufacturer = Manufacturer,
      model = Model,
      firmware = "0.1.0",
    });

    var partId = Guid.Parse(DeviceObjectId.For(
      DeviceObjectId.CameraSource, CameraId, DeviceObjectId.RelayPart, RelayToken));
    var map = new MapDb(db);
    await map.UpdateHierarchyAsync([
      new BaseMarkerDTO { id = Utils.ConvertGuidToObjectId(partId), name = RelayName }
    ]);
    db.DeviceNodes.Add(new DBDeviceNode
    {
      object_id = partId,
      source_kind = DeviceObjectId.CameraSource,
      source_id = CameraId,
      part_kind = DeviceObjectId.RelayPart,
      part_token = RelayToken,
    });

    var type = new DBDiagramType
    {
      id = Guid.NewGuid(),
      name = PanelTypeName,
      source_kind = DeviceObjectId.CameraSource,
      device_manufacturer = Manufacturer,
      device_model = Model,
      auto_attach = true,
    };
    type.regions.Add(new DBDiagramTypeRegion
    {
      diagram_type_id = type.id,
      region_key = RelayRegion,
      geometry = new DBDiagramCoord { width = 0.5, height = 0.5 },
    });
    type.parts.Add(new DBDiagramTypePart
    {
      diagram_type_id = type.id,
      region_key = RelayRegion,
      part_kind = DeviceObjectId.RelayPart,
      match_by = DiagramPartMatch.Token,
      match_value = RelayToken,
    });
    db.DiagramTypes.Add(type);

    var spare = Guid.NewGuid();
    await map.UpdateHierarchyAsync([
      new BaseMarkerDTO { id = Utils.ConvertGuidToObjectId(spare), name = SpareName }
    ]);
    db.Diagrams.Add(new DBDiagram { id = spare });
    await db.SaveChangesAsync();

    return Utils.ConvertGuidToObjectId(spare);
  }

  [Fact]
  public async Task BindingWhileTheSweepReconcilesLeavesOneDiagram()
  {
    await using var first = await MapPostgres.OpenAsync(fresh: true);
    if (first is null)
      return;

    var spare = await SeedAsync(first.Db);
    var catalog = new TestCatalog();
    var settings = new OneRelay();
    await using var second = await MapPostgres.OpenAsync();

    await Task.WhenAll(
      MapPostgres.Diagrams(first.Db, catalog, settings).BindAsync(
        spare, DeviceObjectId.CameraSource, CameraId, null, IDeviceDiagramService.DefaultSlot,
        default),
      MapPostgres.Diagrams(second!.Db, catalog, settings).ReconcileDeviceAsync(
        DeviceObjectId.CameraSource, CameraId, IDeviceDiagramService.DefaultSlot, default));

    Assert.Single(await first.Db.DeviceDiagrams.AsNoTracking().ToListAsync());
    var managed = Assert.Single(await first.Db.DeviceDiagramParts.AsNoTracking().ToListAsync());
    Assert.Equal(RelayRegion, managed.region_key);
    Assert.True(await first.Db.Markers.AnyAsync(m => m.id == managed.replica_object_id));
  }

  [Fact]
  public async Task UnbindingWhileTheSweepReconcilesLeavesTheDiagramUnbound()
  {
    await using var first = await MapPostgres.OpenAsync(fresh: true);
    if (first is null)
      return;

    var spare = await SeedAsync(first.Db);
    var catalog = new TestCatalog();
    var settings = new OneRelay();
    await MapPostgres.Diagrams(first.Db, catalog, settings).BindAsync(
      spare, DeviceObjectId.CameraSource, CameraId, null, IDeviceDiagramService.DefaultSlot, default);
    var replica = (await first.Db.DeviceDiagramParts.AsNoTracking().SingleAsync()).replica_object_id;

    await using var second = await MapPostgres.OpenAsync();

    await Task.WhenAll(
      MapPostgres.Diagrams(first.Db, catalog, settings).UnbindAsync(spare, default),
      MapPostgres.Diagrams(second!.Db, catalog, settings).ReconcileDeviceAsync(
        DeviceObjectId.CameraSource, CameraId, IDeviceDiagramService.DefaultSlot, default));

    // Whichever of the two went second, the answer is the same: the binding and everything it
    // brought are gone, and the diagram the administrator drew is not.
    Assert.Empty(await first.Db.DeviceDiagrams.AsNoTracking().ToListAsync());
    Assert.Empty(await first.Db.DeviceDiagramParts.AsNoTracking().ToListAsync());
    Assert.False(await first.Db.Markers.AnyAsync(m => m.id == replica));
    Assert.True(await first.Db.Markers.AnyAsync(m => m.id == Guid.Parse(spare)));
  }

  [Fact]
  public async Task BindingWhileTheCameraIsRemovedLeavesNoDiagramBehind()
  {
    await using var first = await MapPostgres.OpenAsync(fresh: true);
    if (first is null)
      return;

    var spare = await SeedAsync(first.Db);

    // VmsCfg deletes the camera before it publishes the removal, so the catalog has already
    // forgotten it by the time either the bind or the removal runs.
    var catalog = new TestCatalog { Known = _ => false };
    var settings = new OneRelay();
    await using var second = await MapPostgres.OpenAsync();

    var errors = await Task.WhenAll(
      Capture(async () =>
      {
        await MapPostgres.Diagrams(first.Db, catalog, settings).BindAsync(
          spare, DeviceObjectId.CameraSource, CameraId, null, IDeviceDiagramService.DefaultSlot,
          default);
      }),
      Capture(() => MapPostgres.Nodes(second!.Db, catalog).RemoveCameraAsync(CameraId, default)));

    // Binding to a camera VmsCfg no longer has is refused, not left as a row nothing will remove.
    Assert.IsType<DeviceDiagramException>(errors[0]);
    Assert.Null(errors[1]);
    Assert.Empty(await first.Db.DeviceDiagrams.AsNoTracking().ToListAsync());
    Assert.Empty(await first.Db.DeviceDiagramParts.AsNoTracking().ToListAsync());
  }

  [Fact]
  public async Task RemovingTheCameraTakesABoundDiagramAndItsReplicas()
  {
    await using var connection = await MapPostgres.OpenAsync(fresh: true);
    if (connection is null)
      return;

    var spare = await SeedAsync(connection.Db);
    var catalog = new TestCatalog();
    var settings = new OneRelay();
    await MapPostgres.Diagrams(connection.Db, catalog, settings).BindAsync(
      spare, DeviceObjectId.CameraSource, CameraId, null, IDeviceDiagramService.DefaultSlot, default);
    var replica = (await connection.Db.DeviceDiagramParts.AsNoTracking().SingleAsync())
      .replica_object_id;

    await MapPostgres.Nodes(connection.Db, catalog).RemoveCameraAsync(CameraId, default);

    // The registry rows cascade and are deleted in an order the foreign keys accept; nothing is
    // left pointing at a node that is gone.
    Assert.Empty(await connection.Db.DeviceDiagrams.AsNoTracking().ToListAsync());
    Assert.Empty(await connection.Db.DeviceDiagramParts.AsNoTracking().ToListAsync());
    Assert.False(await connection.Db.Markers.AnyAsync(m => m.id == Guid.Parse(spare)));
    Assert.False(await connection.Db.Markers.AnyAsync(m => m.id == replica));
  }

  [Fact]
  public async Task ReconciliationRemovesTheReplicaOfAnUnconfiguredPart()
  {
    await using var connection = await MapPostgres.OpenAsync(fresh: true);
    if (connection is null)
      return;

    var spare = await SeedAsync(connection.Db);
    var catalog = new TestCatalog();
    await MapPostgres.Diagrams(connection.Db, catalog, new OneRelay()).BindAsync(
      spare, DeviceObjectId.CameraSource, CameraId, null, IDeviceDiagramService.DefaultSlot, default);
    var replica = (await connection.Db.DeviceDiagramParts.AsNoTracking().SingleAsync())
      .replica_object_id;

    // Nobody configures the part any more: the sweep takes its replica and its row away.
    await MapPostgres.Diagrams(connection.Db, catalog).ReconcileDeviceAsync(
      DeviceObjectId.CameraSource, CameraId, IDeviceDiagramService.DefaultSlot, default);

    Assert.Empty(await connection.Db.DeviceDiagramParts.AsNoTracking().ToListAsync());
    Assert.False(await connection.Db.Markers.AnyAsync(m => m.id == replica));
    // The diagram itself is still the device's.
    Assert.Single(await connection.Db.DeviceDiagrams.AsNoTracking().ToListAsync());
  }

  [Fact]
  public async Task UnbindingRecomputesTheDiagramsAlarm()
  {
    await using var connection = await MapPostgres.OpenAsync(fresh: true);
    if (connection is null)
      return;

    var spare = await SeedAsync(connection.Db);
    var catalog = new TestCatalog();
    var settings = new OneRelay();
    var service = MapPostgres.Diagrams(connection.Db, catalog, settings);
    await service.BindAsync(
      spare, DeviceObjectId.CameraSource, CameraId, null, IDeviceDiagramService.DefaultSlot, default);
    var replica = (await connection.Db.DeviceDiagramParts.AsNoTracking().SingleAsync())
      .replica_object_id;

    // The diagram carries an alarm a child put there. Removing the replica is a map deletion, and
    // a map deletion recomputes the alarm state of what the node hung under; a bare node removal
    // would leave the diagram alarmed over a child that no longer exists.
    connection.Db.AlarmStates.Add(new DBAlarmState
    {
      id = Guid.Parse(spare),
      alarm = true,
      children_alarms = 1,
    });
    await connection.Db.SaveChangesAsync();

    await service.UnbindAsync(spare, default);

    var row = await connection.Db.AlarmStates.AsNoTracking()
      .FirstOrDefaultAsync(alarm => alarm.id == Guid.Parse(spare));
    Assert.True(row is null || !row.alarm);
    Assert.False(await connection.Db.Markers.AnyAsync(m => m.id == replica));
  }

  [Fact]
  public async Task TwoBindsOfOneDiagramLeaveOneWinner()
  {
    await using var first = await MapPostgres.OpenAsync(fresh: true);
    if (first is null)
      return;

    var spare = await SeedAsync(first.Db);
    var catalog = new TestCatalog();
    var settings = new OneRelay();
    await using var second = await MapPostgres.OpenAsync();

    var errors = await Task.WhenAll(
      Capture(async () =>
      {
        await MapPostgres.Diagrams(first.Db, catalog, settings).BindAsync(
          spare, DeviceObjectId.CameraSource, CameraId, null, IDeviceDiagramService.DefaultSlot,
          default);
      }),
      Capture(async () =>
      {
        await MapPostgres.Diagrams(second!.Db, catalog, settings).BindAsync(
          spare, DeviceObjectId.CameraSource, OtherCameraId, null, IDeviceDiagramService.DefaultSlot,
          default);
      }));

    Assert.Single(errors, error => error is null);
    Assert.Single(errors, error => error is DeviceDiagramException);

    var row = await first.Db.DeviceDiagrams.AsNoTracking().SingleAsync();
    Assert.True(row.source_id == CameraId || row.source_id == OtherCameraId);
  }
}
