using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using VmsMap.Adapter;
using VmsMap.Clients;
using VmsMap.Db;
using VmsMap.Domain;
using VmsMap.Domain.Arming;

namespace VmsMap.Tests;

/// <summary>
/// The pieces a device-aware service needs in a test. Settings are empty — a part with no row keeps
/// the defaults, which is exactly what the tests exercise — and a camera is disarmed unless a test
/// says otherwise.
/// </summary>
internal sealed class NoDeviceSettings : IDeviceSettingsSource
{
  public Task<IReadOnlyList<DeviceSettingsInfo>> ListAsync(string cameraId, CancellationToken ct) =>
    Task.FromResult<IReadOnlyList<DeviceSettingsInfo>>([]);

  public Task<DeviceSettingsInfo?> FindAsync(
    string cameraId, string kind, string token, CancellationToken ct) =>
    Task.FromResult<DeviceSettingsInfo?>(null);

  public Task<IReadOnlyDictionary<string, IReadOnlyList<DeviceSettingsInfo>>> ListManyAsync(
    IReadOnlyCollection<string> cameraIds, CancellationToken ct) =>
    Task.FromResult<IReadOnlyDictionary<string, IReadOnlyList<DeviceSettingsInfo>>>(
      cameraIds.Distinct(StringComparer.Ordinal)
        .ToDictionary(id => id, _ => (IReadOnlyList<DeviceSettingsInfo>)[], StringComparer.Ordinal));

  public void Invalidate(string cameraId) { }
}

internal sealed class TestCameraState(bool armed = false) : ICameraStateSource
{
  public bool Armed { get; set; } = armed;

  public Task<bool> GetArmedAsync(string cameraId, CancellationToken ct) =>
    Task.FromResult(Armed);

  public Task<IReadOnlyDictionary<string, bool>> GetArmedManyAsync(
    IReadOnlyCollection<string> cameraIds, CancellationToken ct) =>
    Task.FromResult<IReadOnlyDictionary<string, bool>>(
      cameraIds.Distinct(StringComparer.Ordinal)
        .ToDictionary(id => id, _ => Armed, StringComparer.Ordinal));
}

internal static class TestWiring
{
  public static DeviceNodeService DeviceNodes(
    VmsMapDbContext db, MapDb map, MapUpdateService updates, ICameraCatalog? catalog = null) =>
    new(db, map, updates, catalog ?? new AlwaysKnownCatalog(),
        new NoDeviceSettings(), NullLogger<DeviceNodeService>.Instance);

  public static DeviceDiagramService DeviceDiagrams(
    VmsMapDbContext db, MapDb map, MapUpdateService updates, IGeoService geo,
    ICameraCatalog? catalog = null) =>
    new(db, map, updates, new DiagramService(db), new NoDeviceSettings(),
        catalog ?? new AlwaysKnownCatalog(), geo, NullLogger<DeviceDiagramService>.Instance);

  public static EffectiveArmingResolver Arming(
    VmsMapDbContext db, ICameraStateSource? cameraState = null,
    IDeviceSettingsSource? settings = null) =>
    new(db, settings ?? new NoDeviceSettings(), cameraState ?? new TestCameraState());

  public static GuardSensorQuery GuardSensors(
    VmsMapDbContext db, MapDb map, IRightsService? rights = null,
    IDeviceSettingsSource? settings = null, ICameraStateSource? cameraState = null) =>
    new(db, map, rights ?? new RightsService(db), settings ?? new NoDeviceSettings(),
      Arming(db, cameraState, settings));

  public static AlarmStore Alarms(
    VmsMapDbContext db, MapDb map, StatesUpdateService states, ICameraStateSource? cameraState = null)
  {
    var stateService = new StateService(db);
    var writer = new ObjectStateWriter(db, stateService, states);
    var events = new EventsWriter(db, new EventsService(db, map));
    return new AlarmStore(
      db, writer, events, new TestAlarmDefinitions(),
      Arming(db, cameraState), NullLogger<AlarmStore>.Instance);
  }

  public static SensorAlarmService SensorAlarms(
    VmsMapDbContext db, StatesUpdateService states, MapDb map, ICameraStateSource? cameraState = null,
    IDeviceSettingsSource? settings = null) =>
    new(db, settings ?? new NoDeviceSettings(), Arming(db, cameraState),
        Alarms(db, map, states, cameraState));

  private sealed class AlwaysKnownCatalog : ICameraCatalog
  {
    public Task<CameraInfo?> GetAsync(string cameraId, CancellationToken ct) =>
      Task.FromResult<CameraInfo?>(new CameraInfo(cameraId, "Camera " + cameraId, "srv"));

    public Task<List<CameraInfo>> ListAsync(string? serverId, CancellationToken ct) =>
      Task.FromResult(new List<CameraInfo>());
  }
}


/// <summary>The three seeded definitions, as VmsCfg would answer them.</summary>
internal sealed class TestAlarmDefinitions : IAlarmDefinitionsSource
{
  private static readonly IReadOnlyList<AlarmDefinitionInfo> Entries =
  [
    new("00000000-0000-0000-0000-00000000a101", "sensor", "warning", "required"),
    new("00000000-0000-0000-0000-00000000a102", "retention", "warning", "auto_on_normal"),
    new("00000000-0000-0000-0000-00000000a103", "subscription", "warning", "required"),
  ];

  public Task<IReadOnlyList<AlarmDefinitionInfo>> ListAsync(CancellationToken ct) =>
    Task.FromResult(Entries);

  public Task<AlarmDefinitionInfo?> FindAsync(string name, CancellationToken ct) =>
    Task.FromResult(Entries.FirstOrDefault(d => d.Name == name));

  public void Invalidate() { }
}
