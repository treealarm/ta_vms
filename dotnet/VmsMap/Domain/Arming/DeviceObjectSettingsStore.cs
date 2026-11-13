using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using VmsMap.Db;

namespace VmsMap.Domain.Arming
{
  /// <summary>
  /// A logical device object's behaviour: the mode its arming is decided with, and — for a digital
  /// input — the contact state that raises an alarm. A missing row means the defaults, `inherit`
  /// and no alarm, so a caller never has to ask whether the object was configured.
  /// </summary>
  public sealed record DeviceObjectSettings(
    string ObjectId, string Mode, string? TriggerOn, bool AlarmEnabled)
  {
    public static DeviceObjectSettings Default(string objectId) =>
      new(objectId, ArmMode.Inherit, null, false);
  }

  /// <summary>
  /// The map's own copy of a device object's settings. It lives in the map because the map is the
  /// only consumer: the arming resolver, the alarm rule and the control panel read it, while the
  /// services near the hardware read the effective camera flag the map derives from it.
  /// </summary>
  public sealed class DeviceObjectSettingsStore(VmsMapDbContext db)
  {
    private readonly VmsMapDbContext _db = db ?? throw new ArgumentNullException(nameof(db));

    public async Task<DeviceObjectSettings> GetAsync(string objectId, CancellationToken ct)
    {
      var guid = Utils.ConvertObjectIdToGuid(objectId);
      if (guid is null)
        return DeviceObjectSettings.Default(objectId);

      var row = await _db.DeviceObjectSettings.AsNoTracking()
        .FirstOrDefaultAsync(r => r.object_id == guid.Value, ct);

      return row is null ? DeviceObjectSettings.Default(objectId) : ToInfo(objectId, row);
    }

    /// <summary>
    /// The same for a page of objects. Every id asked for is present in the answer — an object with
    /// no row answers with the defaults — so a caller does not have to tell "not asked" from
    /// "nothing configured".
    /// </summary>
    public async Task<IReadOnlyDictionary<string, DeviceObjectSettings>> GetManyAsync(
      IReadOnlyCollection<string> objectIds, CancellationToken ct)
    {
      var result = objectIds
        .Distinct(StringComparer.Ordinal)
        .ToDictionary(id => id, DeviceObjectSettings.Default, StringComparer.Ordinal);
      if (result.Count == 0)
        return result;

      var byGuid = objectIds
        .Select(id => (Id: id, Guid: Utils.ConvertObjectIdToGuid(id)))
        .Where(pair => pair.Guid.HasValue)
        .ToDictionary(pair => pair.Guid!.Value, pair => pair.Id);
      if (byGuid.Count == 0)
        return result;

      var guids = byGuid.Keys.ToList();
      var rows = await _db.DeviceObjectSettings.AsNoTracking()
        .Where(r => guids.Contains(r.object_id))
        .ToListAsync(ct);

      foreach (var row in rows)
        result[byGuid[row.object_id]] = ToInfo(byGuid[row.object_id], row);

      return result;
    }

    /// <summary>
    /// Writes the object's behaviour, creating the row on first use. The caller validates the
    /// vocabulary; the schema repeats the checks for anything that bypasses it.
    /// </summary>
    public async Task SetAsync(
      string objectId, string mode, string? triggerOn, bool alarmEnabled, CancellationToken ct)
    {
      var guid = Utils.ConvertObjectIdToGuid(objectId)
        ?? throw new ArgumentException($"'{objectId}' is not an object id", nameof(objectId));

      var row = await _db.DeviceObjectSettings
        .FirstOrDefaultAsync(r => r.object_id == guid, ct);

      if (row is null)
      {
        _db.DeviceObjectSettings.Add(new DBDeviceObjectSettings
        {
          object_id = guid,
          arm_mode = mode,
          trigger_on = triggerOn,
          alarm_enabled = alarmEnabled,
        });
      }
      else
      {
        row.arm_mode = mode;
        row.trigger_on = triggerOn;
        row.alarm_enabled = alarmEnabled;
      }

      await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Creates the object's row only if the map has none. The one-time import uses this: a row the
    /// map already holds is a later decision and must not be overwritten by an old legacy value.
    /// </summary>
    public async Task SeedAsync(
      string objectId, string mode, string? triggerOn, bool alarmEnabled, CancellationToken ct)
    {
      var guid = Utils.ConvertObjectIdToGuid(objectId)
        ?? throw new ArgumentException($"'{objectId}' is not an object id", nameof(objectId));

      if (await _db.DeviceObjectSettings.AnyAsync(r => r.object_id == guid, ct))
        return;

      _db.DeviceObjectSettings.Add(new DBDeviceObjectSettings
      {
        object_id = guid,
        arm_mode = mode,
        trigger_on = triggerOn,
        alarm_enabled = alarmEnabled,
      });
      await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// The arming mode alone, leaving the alarm rule as it is: an operator's switch changes who
    /// decides the arming, not what the contact means.
    /// </summary>
    public async Task SetModeAsync(string objectId, string mode, CancellationToken ct)
    {
      var guid = Utils.ConvertObjectIdToGuid(objectId)
        ?? throw new ArgumentException($"'{objectId}' is not an object id", nameof(objectId));

      var row = await _db.DeviceObjectSettings
        .FirstOrDefaultAsync(r => r.object_id == guid, ct);

      if (row is null)
        _db.DeviceObjectSettings.Add(new DBDeviceObjectSettings { object_id = guid, arm_mode = mode });
      else
        row.arm_mode = mode;

      await _db.SaveChangesAsync(ct);
    }

    private static DeviceObjectSettings ToInfo(string objectId, DBDeviceObjectSettings row) =>
      new(objectId, row.arm_mode, row.trigger_on, row.alarm_enabled);
  }
}
