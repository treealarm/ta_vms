using Microsoft.EntityFrameworkCore;
using Vms.Cfg;

namespace VmsCfg;

// What the operator configured for a camera's digital inputs and relays.
//
// The table stores only configured parts; a part without a row is not an error and not a gap —
// it keeps the defaults. Validation lives here rather than in the gRPC handler so the same rules
// apply whether a row arrives through the API or through a future import, and so they can be tested
// without a database.
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

  // Arming is a command, not an edit: only the one column moves, so this cannot carry a stale
  // name, trigger or alarm flag over an administrator's concurrent save. Returns false when the
  // camera has no such digital input configured — the operator arms what was set up, and arming
  // never creates the row.
  public async Task<bool> SetArmModeAsync(
    string cameraId, string token, string armMode, CancellationToken ct)
  {
    await using var db = await dbFactory.CreateDbContextAsync(ct);
    var affected = await db.Database.ExecuteSqlInterpolatedAsync($"""
      UPDATE device_settings SET arm_mode = {armMode}
      WHERE camera_id = {cameraId}::uuid AND kind = {DeviceSettingsRules.DigitalInput} AND token = {token};
      """, ct);
    return affected > 0;
  }

  // An upsert, not an insert: saving the same part twice is how an operator edits it, and the
  // natural key (camera, kind, token) is what makes that idempotent.
  public async Task SetAsync(DeviceSettings settings, CancellationToken ct)
  {
    string? triggerOn = string.IsNullOrWhiteSpace(settings.TriggerOn) ? null : settings.TriggerOn;

    await using var db = await dbFactory.CreateDbContextAsync(ct);
    await db.Database.ExecuteSqlInterpolatedAsync($"""
      INSERT INTO device_settings (camera_id, kind, token, name, arm_mode, trigger_on, alarm_enabled)
      VALUES ({settings.CameraId}::uuid, {settings.Kind}, {settings.Token}, {settings.Name ?? string.Empty},
              {settings.ArmMode}, {triggerOn}, {settings.AlarmEnabled})
      ON CONFLICT (camera_id, kind, token) DO UPDATE SET
        name          = EXCLUDED.name,
        arm_mode      = EXCLUDED.arm_mode,
        trigger_on    = EXCLUDED.trigger_on,
        alarm_enabled = EXCLUDED.alarm_enabled;
      """, ct);
  }
}

public static class DeviceSettingsRules
{
  public const string DigitalInput = "digital_input";
  public const string Relay = "relay";

  public const string Inherit = "inherit";
  public const string Armed = "armed";
  public const string Disarmed = "disarmed";

  public const string TriggerOpen = "open";
  public const string TriggerClosed = "closed";

  private static readonly HashSet<string> Kinds = [DigitalInput, Relay];
  private static readonly HashSet<string> ArmModes = [Inherit, Armed, Disarmed];
  private static readonly HashSet<string> Triggers = [TriggerOpen, TriggerClosed];

  /// <summary>
  /// The first thing wrong with an operator's arming command, or null when it is valid. Only the
  /// two modes an operator may write pass: 'inherit' is the administrator's setting, and a relay
  /// is driven by a command of its own rather than armed.
  /// </summary>
  public static string? ValidateArmCommand(string cameraId, string token, string armMode)
  {
    if (!Guid.TryParse(cameraId, out _))
      return "camera_id must be a UUID";
    if (string.IsNullOrWhiteSpace(token))
      return "token must not be empty";
    if (armMode is Armed or Disarmed)
      return null;
    return armMode == Inherit
      ? $"arm_mode '{Inherit}' is an administrator's setting, not an operator's command"
      : $"arm_mode must be '{Armed}' or '{Disarmed}'";
  }

  /// <summary>
  /// The first thing wrong with a settings row, or null when it is valid. The database repeats these
  /// checks; this is what turns them into a sentence the caller can act on.
  /// </summary>
  public static string? Validate(DeviceSettings settings)
  {
    if (!Guid.TryParse(settings.CameraId, out _))
      return "camera_id must be a UUID";
    if (string.IsNullOrWhiteSpace(settings.Token))
      return "token must not be empty";
    if (!Kinds.Contains(settings.Kind))
      return $"kind must be '{DigitalInput}' or '{Relay}'";
    if (!ArmModes.Contains(settings.ArmMode))
      return $"arm_mode must be '{Inherit}', '{Armed}' or '{Disarmed}'";

    var hasTrigger = !string.IsNullOrWhiteSpace(settings.TriggerOn);
    if (hasTrigger && !Triggers.Contains(settings.TriggerOn))
      return $"trigger_on must be '{TriggerOpen}', '{TriggerClosed}' or empty";

    if (settings.Kind == Relay)
    {
      if (settings.ArmMode != Inherit || hasTrigger || settings.AlarmEnabled)
        return "a relay carries no arming or alarm settings";
      return null;
    }

    if (settings.AlarmEnabled && !hasTrigger)
      return "alarm_enabled requires trigger_on";

    return null;
  }
}
