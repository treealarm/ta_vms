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
using VmsMap.Domain.Control;

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

internal sealed class TestCameraState(bool armed = false, string armMode = "inherit") : ICameraStateSource
{
  public bool Armed { get; set; } = armed;

  /// <summary>Every camera answers the same mode: a test that needs two answers makes its own.</summary>
  public string ArmMode { get; set; } = armMode;

  public Task<CameraArmState?> GetStateAsync(string cameraId, CancellationToken ct) =>
    Task.FromResult<CameraArmState?>(new CameraArmState(cameraId, Armed, ArmMode));

  public Task<IReadOnlyDictionary<string, CameraArmState>> GetStatesAsync(
    IReadOnlyCollection<string> cameraIds, CancellationToken ct) =>
    Task.FromResult<IReadOnlyDictionary<string, CameraArmState>>(
      cameraIds.Distinct(StringComparer.Ordinal)
        .ToDictionary(id => id, id => new CameraArmState(id, Armed, ArmMode), StringComparer.Ordinal));
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

  public static CameraArmingReconciler CameraArming(
    VmsMapDbContext db, MapDb map, ICameraStateSource? cameraState = null,
    ICfgArmingWriter? writer = null)
  {
    var state = cameraState ?? new TestCameraState();
    return new(db, Arming(db, state), state, writer ?? new TestCfgArmingWriter(state), map,
      NullLogger<CameraArmingReconciler>.Instance);
  }

  public static ControlObjectQuery ControlObjects(
    VmsMapDbContext db, MapDb map, IRightsService? rights = null,
    IDeviceSettingsSource? settings = null, ICameraStateSource? cameraState = null)
  {
    var states = cameraState ?? new TestCameraState();
    return new(db, map, rights ?? new RightsService(db), settings ?? new NoDeviceSettings(),
      states, Arming(db, states, settings));
  }

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
        Alarms(db, map, states, cameraState), map,
        NullLogger<SensorAlarmService>.Instance);

  public static NodeArmingService NodeArming(
    VmsMapDbContext db, MapDb map, ICfgArmingWriter? writer = null,
    ICameraStateSource? cameraState = null, IDeviceSettingsSource? settings = null)
  {
    var camera = cameraState ?? new TestCameraState();
    var source = settings ?? new NoDeviceSettings();
    return new(db, Arming(db, camera, source), source, camera, writer ?? new TestCfgArmingWriter(camera),
      map, new EventsWriter(db, new EventsService(db, map)),
      NullLogger<NodeArmingService>.Instance);
  }

  /// <summary>A stand-in for VmsCfg: it records the command, and keeps a camera's flag the way
  /// VmsCfg would so the read that follows the write sees it.</summary>
  internal sealed class TestCfgArmingWriter(ICameraStateSource? cameraState = null) : ICfgArmingWriter
  {
    public List<(string CameraId, string Token, string Mode)> PartModes { get; } = [];

    public List<(string CameraId, bool Armed)> CameraFlags { get; } = [];

    public Task SetPartArmModeAsync(
      string cameraId, string token, string mode, CancellationToken ct)
    {
      PartModes.Add((cameraId, token, mode));
      return Task.CompletedTask;
    }

    public Task SetCameraArmedAsync(string cameraId, bool armed, CancellationToken ct)
    {
      CameraFlags.Add((cameraId, armed));
      if (cameraState is TestCameraState test)
      {
        test.Armed = armed;
        test.ArmMode = armed ? "armed" : "disarmed";
      }
      return Task.CompletedTask;
    }

    /// <summary>The derived writes, recorded the way VmsCfg records them: only a change counts.</summary>
    public List<(string CameraId, bool Armed)> EffectiveFlags { get; } = [];

    public Task<bool> SetCameraEffectiveArmedAsync(string cameraId, bool armed, CancellationToken ct)
    {
      if (cameraState is not TestCameraState test || test.ArmMode != "inherit")
        return Task.FromResult(false);
      if (test.Armed == armed)
        return Task.FromResult(false);

      test.Armed = armed;
      EffectiveFlags.Add((cameraId, armed));
      return Task.FromResult(true);
    }
  }

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
