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
  /// <summary>
  /// The gRPC layer for a call that only touches the type dictionary: everything it is not given
  /// here is a capability <c>DeleteDiagramTypes</c> does not use. In-memory on purpose — like
  /// <see cref="MapPostgres.Grpc"/> but without the scratch database, so the RPC contract is
  /// checked even when no test DSN is set.
  /// </summary>
  public static VmsMap.Grpc.MapServiceImpl MapRpc(VmsMapDbContext db) => new(
    db, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!,
    null!, new DiagramTypeService(db), null!, null!, null!, null!, null!, null!, null!, null!,
    null!, null!, null!, null!, null!, null!);

  public static DeviceNodeService DeviceNodes(
    VmsMapDbContext db, MapDb map, MapUpdateService updates, ICameraCatalog? catalog = null) =>
    new(db, map, updates, catalog ?? new AlwaysKnownCatalog(),
        new NoDeviceSettings(), NullLogger<DeviceNodeService>.Instance);

  public static DeviceDiagramReconciler DiagramReconciler(
    VmsMapDbContext db, MapDb map, MapUpdateService updates,
    IDeviceSettingsSource? settings = null) =>
    new(db, map, updates, new DiagramService(db), settings ?? new NoDeviceSettings(),
        NullLogger<DeviceDiagramReconciler>.Instance);

  public static DeviceDiagramService DeviceDiagrams(
    VmsMapDbContext db, MapDb map, MapUpdateService updates, IGeoService geo,
    ICameraCatalog? catalog = null, IDeviceSettingsSource? settings = null) =>
    new(db, map, updates, new DiagramService(db), catalog ?? new AlwaysKnownCatalog(), geo,
        DiagramReconciler(db, map, updates, settings));

  public static DeviceObjectSettingsStore ObjectSettings(VmsMapDbContext db) => new(db);

  public static EffectiveArmingResolver Arming(
    VmsMapDbContext db, ICameraStateSource? cameraState = null,
    DeviceObjectSettingsStore? objectSettings = null) =>
    new(db, objectSettings ?? ObjectSettings(db), cameraState ?? new TestCameraState());

  public static CameraArmingReconciler CameraArming(
    VmsMapDbContext db, MapDb map, ICameraStateSource? cameraState = null,
    ICfgArmingWriter? writer = null)
  {
    var state = cameraState ?? new TestCameraState();
    return new(db, Arming(db, state), ObjectSettings(db), state,
      writer ?? new TestCfgArmingWriter(state), map,
      NullLogger<CameraArmingReconciler>.Instance);
  }

  public static ControlObjectQuery ControlObjects(
    VmsMapDbContext db, MapDb map, IRightsService? rights = null,
    IDeviceSettingsSource? settings = null, ICameraStateSource? cameraState = null,
    DeviceObjectSettingsStore? objectSettings = null)
  {
    var states = cameraState ?? new TestCameraState();
    var own = objectSettings ?? ObjectSettings(db);
    return new(db, map, rights ?? new RightsService(db), settings ?? new NoDeviceSettings(),
      own, states, Arming(db, states, own));
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
    DeviceObjectSettingsStore? objectSettings = null)
  {
    var own = objectSettings ?? ObjectSettings(db);
    return new(db, own, Arming(db, cameraState, own),
      Alarms(db, map, states, cameraState), map,
      NullLogger<SensorAlarmService>.Instance);
  }

  public static NodeArmingService NodeArming(
    VmsMapDbContext db, MapDb map, ICfgArmingWriter? writer = null,
    ICameraStateSource? cameraState = null, IDeviceSettingsSource? settings = null,
    TimeProvider? clock = null)
  {
    var camera = cameraState ?? new TestCameraState();
    var source = settings ?? new NoDeviceSettings();
    var own = ObjectSettings(db);
    var arming = Arming(db, camera, own);
    var cfg = writer ?? new TestCfgArmingWriter(camera);
    var states = new StatesUpdateService(new StateService(db), map, db);
    // Every device write derives once, whatever facade called it: the tests exercise the same
    // helper the process uses.
    var derivation = new DeviceArmingDerivation(
      new CameraArmingReconciler(
        db, arming, own, camera, cfg, map, NullLogger<CameraArmingReconciler>.Instance),
      new SensorAlarmService(
        db, own, arming, Alarms(db, map, states, camera), map,
        NullLogger<SensorAlarmService>.Instance),
      NullLogger<DeviceArmingDerivation>.Instance);
    return new(db, arming, source, own, derivation,
      new EventsWriter(db, new EventsService(db, map)),
      clock ?? TimeProvider.System,
      NullLogger<NodeArmingService>.Instance);
  }

  /// <summary>A stand-in for VmsCfg: it records the flag the map writes, and keeps it so the read
  /// that follows sees it. Only a change counts, as the real column reports.</summary>
  internal sealed class TestCfgArmingWriter(ICameraStateSource? cameraState = null) : ICfgArmingWriter
  {
    public List<(string CameraId, bool Armed)> CameraFlags { get; } = [];

    /// <summary>How many times the map asked for a write, changed or not.</summary>
    public int Calls { get; private set; }

    /// <summary>Runs before the write lands: how a test makes one camera's write fail.</summary>
    public Func<string, bool, Task>? BeforeWrite { get; set; }

    /// <summary>Runs once the write has landed: how a test plays the world changing under it.</summary>
    public Func<string, bool, Task>? AfterWrite { get; set; }

    public async Task<bool> SetCamArmedAsync(string cameraId, bool armed, CancellationToken ct)
    {
      Calls += 1;
      if (BeforeWrite is not null)
        await BeforeWrite(cameraId, armed);

      if (cameraState is TestCameraState test && test.Armed == armed)
        return false;

      CameraFlags.Add((cameraId, armed));
      if (cameraState is TestCameraState state)
        state.Armed = armed;
      if (AfterWrite is not null)
        await AfterWrite(cameraId, armed);
      return true;
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
  private const string WarningSeverity = "warning";
  private const string RequiredAction = "required";

  private static readonly IReadOnlyList<AlarmDefinitionInfo> Entries =
  [
    new("00000000-0000-0000-0000-00000000a101", "sensor", WarningSeverity, RequiredAction),
    new("00000000-0000-0000-0000-00000000a102", "retention", WarningSeverity, "auto_on_normal"),
    new("00000000-0000-0000-0000-00000000a103", "subscription", WarningSeverity, RequiredAction),
  ];

  public Task<IReadOnlyList<AlarmDefinitionInfo>> ListAsync(CancellationToken ct) =>
    Task.FromResult(Entries);

  public Task<AlarmDefinitionInfo?> FindAsync(string name, CancellationToken ct) =>
    Task.FromResult(Entries.FirstOrDefault(d => d.Name == name));

  public void Invalidate() { }
}
