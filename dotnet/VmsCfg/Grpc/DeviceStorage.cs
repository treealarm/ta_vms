using Microsoft.EntityFrameworkCore;
using Vms.Cfg;

namespace VmsCfg;

// What an operator configured for a camera's digital inputs and relays.
//
// The table stores only configured parts; a part without a row is not an error and not a gap —
// it keeps the defaults. What it writes is the part's name: the behaviour — who decides its
// arming and what its contact means — is a logical object's setting and lives in VmsMap. The
// `arm_mode`, `trigger_on` and `alarm_enabled` columns remain readable in this release because
// the map imports them once (release D1); they go away in D2.
public class DeviceStorage(IDbContextFactory<VmsCfgDbContext> dbFactory)
{
  public async Task<List<DeviceSettings>> ListAsync(string cameraId, CancellationToken ct)
  {
    if (!Guid.TryParse(cameraId, out _))
      return [];

    await using var db = await dbFactory.CreateDbContextAsync(ct);
    return await db.DeviceSettings
      .AsNoTracking()
      .Where(x => x.CameraId == cameraId)
      .OrderBy(x => x.Kind)
      .ThenBy(x => x.Token)
      .Select(x => new DeviceSettings
      {
        CameraId = x.CameraId,
        Kind = x.Kind,
        Token = x.Token,
        Name = x.Name,
        ArmMode = x.ArmMode,
        TriggerOn = x.TriggerOn ?? string.Empty,
        AlarmEnabled = x.AlarmEnabled,
      })
      .ToListAsync(ct);
  }

  // The same rows for several cameras in one query: the operator's guarding list fills a page and
  // asks for that page's cameras at once, not one round trip per camera.
  public async Task<List<DeviceSettings>> ListManyAsync(
    IReadOnlyCollection<string> cameraIds, CancellationToken ct)
  {
    var ids = cameraIds
      .Where(id => Guid.TryParse(id, out _))
      .Distinct(StringComparer.Ordinal)
      .ToList();
    if (ids.Count == 0)
      return [];

    await using var db = await dbFactory.CreateDbContextAsync(ct);
    return await db.DeviceSettings
      .AsNoTracking()
      .Where(x => ids.Contains(x.CameraId))
      .OrderBy(x => x.CameraId)
      .ThenBy(x => x.Kind)
      .ThenBy(x => x.Token)
      .Select(x => new DeviceSettings
      {
        CameraId = x.CameraId,
        Kind = x.Kind,
        Token = x.Token,
        Name = x.Name,
        ArmMode = x.ArmMode,
        TriggerOn = x.TriggerOn ?? string.Empty,
        AlarmEnabled = x.AlarmEnabled,
      })
      .ToListAsync(ct);
  }

  // The name only. An upsert, not an insert: saving the same part twice is how an operator edits
  // it, and the natural key (camera, kind, token) is what makes that idempotent.
  //
  // The insert leaves the behaviour columns at their defaults and the update does not name them at
  // all, so a row written by an older release keeps what it had until the map's one-time import
  // has read it. Writing them here would make this a second source of a decision the map owns.
  public async Task SetAsync(DeviceSettings settings, CancellationToken ct)
  {
    await using var db = await dbFactory.CreateDbContextAsync(ct);
    await db.Database.ExecuteSqlInterpolatedAsync($"""
      INSERT INTO device_settings (camera_id, kind, token, name)
      VALUES ({settings.CameraId}::uuid, {settings.Kind}, {settings.Token}, {settings.Name ?? string.Empty})
      ON CONFLICT (camera_id, kind, token) DO UPDATE SET
        name = EXCLUDED.name;
      """, ct);
  }
}

public static class DeviceSettingsRules
{
  public const string DigitalInput = "digital_input";
  public const string Relay = "relay";

  private static readonly HashSet<string> Kinds = [DigitalInput, Relay];

  /// <summary>
  /// The first thing wrong with a part's identity, or null when it is valid. The behaviour the
  /// request may still carry is not checked here: it is not stored, and the map validates what it
  /// receives.
  /// </summary>
  public static string? Validate(DeviceSettings settings)
  {
    if (!Guid.TryParse(settings.CameraId, out _))
      return "camera_id must be a UUID";
    if (string.IsNullOrWhiteSpace(settings.Token))
      return "token must not be empty";
    if (!Kinds.Contains(settings.Kind))
      return $"kind must be '{DigitalInput}' or '{Relay}'";

    return null;
  }
}
