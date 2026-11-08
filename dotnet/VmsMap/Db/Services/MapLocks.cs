using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;

namespace VmsMap.Db
{
  /// <summary>
  /// The map's transitive locks in the one order they may be taken. The camera-state lock
  /// serialises the read-modify-write of an object's full state set; the alarm lock serialises the
  /// aggregation that follows it. Taking them in the other order in one handler and this order in
  /// another is a deadlock, so no new code writes <c>pg_advisory_xact_lock</c> by hand — a grep test
  /// enforces that, and this class is the only place the lock names live.
  /// </summary>
  internal static class MapLocks
  {
    public const string CameraState = "ta_map_camera_state";
    public const string Alarm = "ta_map_alarm";

    /// <summary>Installing or removing the demo world is once-only work for the whole deployment.</summary>
    private const long DemoInstallKey = 0x74615f64656d6f31; // "ta_demo1"

    /// <summary>Must be taken before the alarm lock.</summary>
    public static Task AcquireCameraStateAsync(VmsMapDbContext db, CancellationToken ct = default) =>
      AcquireAsync(db, CameraState, ct);

    /// <summary>Taken after the camera-state lock when both are needed.</summary>
    public static Task AcquireAlarmAsync(VmsMapDbContext db, CancellationToken ct = default) =>
      AcquireAsync(db, Alarm, ct);

    /// <summary>
    /// Both of the map's transitive locks, in the one order they may be taken. An alarm transition
    /// changes a row, the object's state set and the aggregation above it, so it holds both; taking
    /// them through this method is what keeps every path on the same prefix.
    /// </summary>
    public static async Task AcquireAllAsync(VmsMapDbContext db, CancellationToken ct = default)
    {
      await AcquireCameraStateAsync(db, ct);
      await AcquireAlarmAsync(db, ct);
    }

    /// <summary>
    /// Serialises one device's diagram reconciliation. A snapshot and a configuration change
    /// arrive independently and both may find no diagram to reconcile; without this they would
    /// both create one. The key is the registry key, not user text: ids are coerced UUIDs and the
    /// slot is a constant today.
    /// </summary>
    public static async Task AcquireDeviceDiagramAsync(
      VmsMapDbContext db, string sourceKind, string sourceId, string slot,
      CancellationToken ct = default)
    {
      if (!db.Database.IsRelational())
        return;

      var name = $"ta_map_device_diagram:{sourceKind}:{sourceId}:{slot}";
      await db.Database.ExecuteSqlInterpolatedAsync(
        $"SELECT pg_advisory_xact_lock(hashtext({name}));", ct);
    }

    public static async Task AcquireDemoInstallAsync(VmsMapDbContext db, CancellationToken ct = default)
    {
      if (!db.Database.IsRelational())
        return;

      // Parameterised, even though the key is a constant: raw interpolation is what the EF
      // analyzer refuses, and there is no reason to ask for an exception here.
      await db.Database.ExecuteSqlInterpolatedAsync(
        $"SELECT pg_advisory_xact_lock({DemoInstallKey});", ct);
    }

    private static async Task AcquireAsync(
      VmsMapDbContext db, string name, CancellationToken ct)
    {
      if (!db.Database.IsRelational())
        return;

      // The name is one of the two constants above, never anything built from input; the
      // parameter keeps the statement from being assembled by interpolation.
      await db.Database.ExecuteSqlInterpolatedAsync(
        $"SELECT pg_advisory_xact_lock(hashtext({name}));", ct);
    }
  }
}
