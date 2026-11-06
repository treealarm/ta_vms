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

    public static async Task AcquireDemoInstallAsync(VmsMapDbContext db, CancellationToken ct = default)
    {
      if (!db.Database.IsRelational())
        return;

      await db.Database.ExecuteSqlRawAsync(
        $"SELECT pg_advisory_xact_lock({DemoInstallKey});", ct);
    }

    private static async Task AcquireAsync(
      VmsMapDbContext db, string name, CancellationToken ct)
    {
      if (!db.Database.IsRelational())
        return;

      // The name is one of the two constants above, never anything built from input.
      await db.Database.ExecuteSqlRawAsync(
        $"SELECT pg_advisory_xact_lock(hashtext('{name}'));", ct);
    }
  }
}
