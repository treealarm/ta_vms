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
using VmsMap.Domain.Arming;

namespace VmsMap.Tests;

// The one-time move of VmsCfg's arming columns into the map: an explicitly armed camera and a
// configured input keep their behaviour across the upgrade, and anything the map already holds is
// not overwritten.
public class LegacyArmingImporterTests
{
  private const string CameraId = "3f2504e0-4f89-11d3-9a0c-0305e82c3301";
  private const string Token = "input1";

  private sealed class Catalog : ICameraCatalog
  {
    public Task<CameraInfo?> GetAsync(string cameraId, CancellationToken ct) =>
      Task.FromResult<CameraInfo?>(new CameraInfo(cameraId, "Camera " + cameraId, "srv"));

    public Task<List<CameraInfo>> ListAsync(string? serverId, CancellationToken ct) =>
      Task.FromResult<List<CameraInfo>>([]);
  }

  private sealed class LegacySettings(DeviceSettingsInfo? row) : IDeviceSettingsSource
  {
    public Task<IReadOnlyList<DeviceSettingsInfo>> ListAsync(string cameraId, CancellationToken ct) =>
      Task.FromResult<IReadOnlyList<DeviceSettingsInfo>>(row is null ? [] : [row]);

    public Task<DeviceSettingsInfo?> FindAsync(
      string cameraId, string kind, string token, CancellationToken ct) => Task.FromResult(row);

    public Task<IReadOnlyDictionary<string, IReadOnlyList<DeviceSettingsInfo>>> ListManyAsync(
      IReadOnlyCollection<string> cameraIds, CancellationToken ct) =>
      Task.FromResult<IReadOnlyDictionary<string, IReadOnlyList<DeviceSettingsInfo>>>(
        cameraIds.ToDictionary(
          id => id,
          _ => (IReadOnlyList<DeviceSettingsInfo>)(row is null ? [] : [row]),
          StringComparer.Ordinal));

    public void Invalidate(string cameraId) { }
  }

  private sealed class Marker(bool imported) : ILegacyImportMarker
  {
    public bool Imported { get; private set; } = imported;

    public Task<bool> IsImportedAsync(CancellationToken ct) => Task.FromResult(Imported);

    public Task MarkImportedAsync(CancellationToken ct)
    {
      Imported = true;
      return Task.CompletedTask;
    }
  }

  private sealed record Fixture(
    VmsMapDbContext Db, DeviceObjectSettingsStore Store, Marker Marker, DeviceNodeService Nodes);

  private static async Task<Fixture> ImportAsync(
    string cameraMode = ArmMode.Inherit, DeviceSettingsInfo? settings = null, bool imported = false)
  {
    var options = new DbContextOptionsBuilder<VmsMapDbContext>()
      .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
      .Options;

    var db = new VmsMapDbContext(options);
    var map = new MapDb(db);
    var stateService = new StateService(db);
    var states = new StatesUpdateService(stateService, map, db);
    var levels = new LevelService(db);
    var tracks = new TracksUpdateService(map, new TrackService(db, levels), new GeoService(db, levels));
    var updates = new MapUpdateService(map, tracks, states);
    var nodes = TestWiring.DeviceNodes(db, map, updates, new Catalog());
    var store = TestWiring.ObjectSettings(db);
    var marker = new Marker(imported);
    var importer = new LegacyArmingImporter(
      store, new TestCameraState(armMode: cameraMode), new LegacySettings(settings), nodes, marker,
      NullLogger<LegacyArmingImporter>.Instance);

    await importer.EnsureImportedAsync(
      [new CameraInfo(CameraId, "Camera", "srv")], CancellationToken.None);

    return new Fixture(db, store, marker, nodes);
  }

  private static DeviceSettingsInfo InputRow(string mode = "inherit", string trigger = "closed") =>
    new(CameraId, DeviceObjectId.DigitalInputPart, Token, "Door", mode, trigger, true);

  [Fact]
  public async Task AnExplicitlyArmedCameraKeepsItsMode()
  {
    var f = await ImportAsync(cameraMode: ArmMode.Armed);

    Assert.Equal(ArmMode.Armed,
      (await f.Store.GetAsync(CameraObjectId.For(CameraId), default)).Mode);
    Assert.True(f.Marker.Imported);
  }

  [Fact]
  public async Task AConfiguredInputKeepsItsRule()
  {
    var f = await ImportAsync(settings: InputRow(trigger: "closed"));

    var input = DeviceObjectId.For(
      DeviceObjectId.CameraSource, CameraId, DeviceObjectId.DigitalInputPart, Token);
    var seeded = await f.Store.GetAsync(input, default);
    Assert.Equal("closed", seeded.TriggerOn);
    Assert.True(seeded.AlarmEnabled);
  }

  [Fact]
  public async Task AnInheritingCameraWithNoInputRuleWritesNothing()
  {
    var f = await ImportAsync(settings: InputRow(mode: "inherit", trigger: "closed") with
    {
      TriggerOn = null,
      AlarmEnabled = false,
    });

    Assert.False(await f.Db.DeviceObjectSettings.AnyAsync());
  }

  [Fact]
  public async Task AMarkedInstallationIsNotImportedAgain()
  {
    var f = await ImportAsync(cameraMode: ArmMode.Armed, imported: true);

    Assert.False(await f.Db.DeviceObjectSettings.AnyAsync());
  }

  [Fact]
  public async Task ARowTheMapAlreadyHoldsIsNotOverwritten()
  {
    var f = await ImportAsync(cameraMode: ArmMode.Armed);
    await f.Store.SetModeAsync(CameraObjectId.For(CameraId), ArmMode.Disarmed, default);

    // A later import of the same installation must leave the map's later decision alone.
    var importer = new LegacyArmingImporter(
      f.Store, new TestCameraState(armMode: ArmMode.Armed), new LegacySettings(null), f.Nodes,
      new Marker(false), NullLogger<LegacyArmingImporter>.Instance);
    await importer.EnsureImportedAsync(
      [new CameraInfo(CameraId, "Camera", "srv")], CancellationToken.None);

    Assert.Equal(ArmMode.Disarmed,
      (await f.Store.GetAsync(CameraObjectId.For(CameraId), default)).Mode);
  }
}
