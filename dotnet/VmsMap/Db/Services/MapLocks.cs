using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;

namespace VmsMap.Db
{
  /// <summary>
  /// The map's transitive locks in the one order they may be taken:
  /// <c>camera-state → camera-placement → alarm → device-diagram → hierarchy</c>.
  ///
  /// The camera-state lock serialises the read-modify-write of an object's full state set; the alarm
  /// lock serialises the aggregation that follows it. A placement comes before the alarm because
  /// removing one recomputes alarms inside the same transaction; the hierarchy lock comes last
  /// because the paths that write the tree and then recompute alarms — <see cref="MapUpdateService"/>
  /// — take the alarm lock first, and <c>DeviceDiagramService</c> already holds camera-state and
  /// alarm when it moves a node. Taking them in another order in one handler and this order in
  /// another is a deadlock, so no new code writes <c>pg_advisory_xact_lock</c> by hand — a grep test
  /// enforces that, and this class is the only place the lock names live.
  /// </summary>
  internal static class MapLocks
  {
    public const string CameraState = "ta_map_camera_state";
    public const string Alarm = "ta_map_alarm";

    /// <summary>The tree itself: a move reads it and writes it, and the two must not interleave.</summary>
    public const string Hierarchy = "ta_map_hierarchy";

    /// <summary>Installing or removing the demo world is once-only work for the whole deployment.</summary>
    private const long DemoInstallKey = 0x74615f64656d6f31; // "ta_demo1"

    /// <summary>Must be taken before the alarm lock.</summary>
    public static Task AcquireCameraStateAsync(VmsMapDbContext db, CancellationToken ct = default) =>
      AcquireAsync(db, CameraState, ct);

    /// <summary>Taken after the camera-state lock when both are needed.</summary>
    public static Task AcquireAlarmAsync(VmsMapDbContext db, CancellationToken ct = default) =>
      AcquireAsync(db, Alarm, ct);

    /// <summary>
    /// Serialises tree changes for the whole deployment. A cycle is a property of the tree, not of
    /// one node, so the lock cannot be keyed by node; tree writes are configuration changes, not a
    /// hot path, which is what makes one key affordable. It is taken last: a writer that also
    /// recomputes alarms has already taken the alarm lock by then.
    /// </summary>
    public static Task AcquireHierarchyAsync(VmsMapDbContext db, CancellationToken ct = default) =>
      AcquireAsync(db, Hierarchy, ct);

    /// <summary>
    /// Serialises one camera's placements. Two requests for the same canvas both find no picture
    /// and each would create one; the canvas is derived (geo_objects.canvas_id), so the schema
    /// cannot say "one picture per camera per canvas" and the write is serialised per camera.
    /// </summary>
    public static async Task AcquireCameraPlacementAsync(
      VmsMapDbContext db, string cameraId, CancellationToken ct = default)
    {
      if (!db.Database.IsRelational())
        return;

      var name = $"ta_map_camera_placement:{cameraId}";
      await db.Database.ExecuteSqlInterpolatedAsync(
        $"SELECT pg_advisory_xact_lock(hashtext({name}));", ct);
    }

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
    /// Serialises one device's diagram work: reconciliation, placement, binding and the removal
    /// that takes all of it away. The key is the device, not the slot: the device's canonical
    /// diagrams may differ by slot, but these operations of one device have to exclude each other.
    /// It is the registry key, not user text — ids are coerced UUIDs.
    /// </summary>
    public static async Task AcquireDeviceDiagramAsync(
      VmsMapDbContext db, string sourceKind, string sourceId,
      CancellationToken ct = default)
    {
      if (!db.Database.IsRelational())
        return;

      var name = $"ta_map_device_diagram:{sourceKind}:{sourceId}";
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

      // The name is one of the constants above, never anything built from input; the
      // parameter keeps the statement from being assembled by interpolation.
      await db.Database.ExecuteSqlInterpolatedAsync(
        $"SELECT pg_advisory_xact_lock(hashtext({name}));", ct);
    }
  }
}
