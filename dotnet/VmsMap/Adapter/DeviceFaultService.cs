using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using VmsMap.Db;

namespace VmsMap.Adapter
{
  /// <summary>
  /// Keeps a part's fault alarm in step with the presence flag the snapshot wrote. The alarm is the
  /// operator's task; <c>device_node.present</c> remains the truth about whether the part is there.
  /// The service is called inside the snapshot transaction, so the flag and the alarm commit or
  /// roll back together.
  /// </summary>
  /// <remarks>
  /// The rule is independent of the input's alarm settings and of arming: a part the device no
  /// longer reports is a fault whether its contact alarm is enabled, the node is armed, or the part
  /// is a relay. Only an accepted successful manifest may say a part is gone, and only one may say
  /// it is back; an unanswered camera changes nothing.
  /// </remarks>
  internal sealed class DeviceFaultService
  {
    /// <summary>What a raise records as its trigger.</summary>
    public const string PartAbsentTrigger = "part_absent";

    /// <summary>
    /// One part's fault address. The canonical node id is the identity: it is derived from the
    /// device's own key and does not change while the part exists, and the partial unique index on
    /// open alarms keeps one fault per node.
    /// </summary>
    public static string DedupKey(Guid objectId) => $"fault:{objectId}";

    private readonly VmsMapDbContext _db;
    private readonly AlarmStore _alarms;

    public DeviceFaultService(VmsMapDbContext db, AlarmStore alarms)
    {
      _db = db ?? throw new ArgumentNullException(nameof(db));
      _alarms = alarms ?? throw new ArgumentNullException(nameof(alarms));
    }

    /// <summary>
    /// Applies the rule to every part of one camera from the rows the snapshot just updated. An
    /// absent part gets an active fault; a part that reports again gets its fault returned, where
    /// it waits for an operator's reset. An open active fault is left alone, and a returned one is
    /// revived by the raise. The query is one read for the whole camera, so a snapshot with no
    /// faults costs no per-part round trip.
    /// </summary>
    public async Task ApplyAsync(
      IReadOnlyCollection<DBDeviceNode> parts, CancellationToken ct)
    {
      if (parts.Count == 0)
        return;

      var ids = parts.Select(p => p.object_id).ToList();
      var open = await _db.Alarms.AsNoTracking()
        .Where(a => ids.Contains(a.object_id)
                    && a.source == AlarmSource.Fault
                    && a.status != AlarmStatus.Closed)
        .ToDictionaryAsync(a => a.object_id, ct);

      foreach (var part in parts)
      {
        open.TryGetValue(part.object_id, out var alarm);

        if (!part.present)
        {
          // The manifest says the part is gone. A returned fault is revived; an active one
          // already says the same thing.
          if (alarm is null || alarm.status == AlarmStatus.Returned)
          {
            await _alarms.RaiseAsync(new RaiseAlarmSpec(
              ObjectId: part.object_id,
              Source: AlarmSource.Fault,
              DedupKey: DedupKey(part.object_id),
              Severity: AlarmSeverity.Warning,
              Trigger: PartAbsentTrigger), ct);
          }
          continue;
        }

        // The part reports again: the condition is gone, the alarm is not. Reset is the
        // operator's action, so the row stays open as returned.
        if (alarm is { condition_active: true })
          await _alarms.NormalFaultAsync(part.object_id, alarm.dedup_key, ct);
      }
    }
  }
}
